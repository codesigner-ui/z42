# Design: D2a — `MulticastAction<T>`

## Architecture

```
┌────────── stdlib `z42.core/src/MulticastAction.z42` ───────────┐
│                                                                  │
│  public sealed class MulticastAction<T> {                        │
│      private List<Action<T>> _handlers = new List<Action<T>>(); │
│                                                                  │
│      public int Count => _handlers.Count;                       │
│                                                                  │
│      public IDisposable Subscribe(Action<T> handler) {           │
│          _handlers.Add(handler);                                 │
│          return new Disposable(() => Unsubscribe(handler));     │
│      }                                                           │
│                                                                  │
│      public void Unsubscribe(Action<T> handler) {                │
│          _handlers.Remove(handler);   // first-match by ref      │
│      }                                                           │
│                                                                  │
│      public void Invoke(T arg, bool continueOnException = false) {│
│          var snapshot = _handlers.ToArray();   // COW             │
│          foreach (var h in snapshot) h(arg);                    │
│      }                                                           │
│  }                                                               │
└──────────────────────────────────────────────────────────────────┘
```

零 IR / VM 变更 —— 这是一个普通的 generic class，编译器现有路径全部支持。

## Decisions

### Decision 1: 双 vec storage (strong / advanced) 在 D2a 暂不实现

**问题**：delegates-events.md §9.1 优化 A 要求 `MulticastStorage<T>` 内部分两个 vec：`strong: Vec<Action<T>>` 走 fast path（无 wrapper）；`advanced: Vec<Box<dyn ISubscription<Action<T>>>>` 走 slow path（weak/once/...）。

**决定**：D2a 仅实现 strong vec（`List<Action<T>> _handlers`）；advanced vec + ISubscription 体系在 D2b 与三个性能优化一起做。

理由：
1. ISubscription 接口定义在 D2b；D2a 提前埋 advanced 字段会引入未定义类型
2. D2a 无 wrapper 场景 → 整个 invoke 走 fast loop（自然达到优化 A 的"strong 路径零开销"目标）
3. D2b 加 ISubscription 时再加 advanced 字段 + 双 loop 即可

### Decision 2: COW 用 `.ToArray()` 复制全数组

**问题**：snapshot 实现方式？

**选项**：
- A: `_handlers.ToArray()` 每次 invoke 复制（简单）
- B: 引入版本号 + 写时复制（复杂，性能更好）
- C: 不可变持久化结构（需要新 stdlib 数据结构）

**决定**：**选 A**。理由：
1. delegates-events.md §4.3 文字描述就是"COW snapshot"，最自然实现就是数组复制
2. handler 数通常 ≤10，复制成本可忽略
3. 性能优化（写时复制 / immutable structure）等基准数据驱动时再做

### Decision 3: handler 比较走 reference equality

**问题**：`Unsubscribe(handler)` 怎么 match？

**决定**：**reference equality**（`List<T>.Remove` 默认行为）。

理由：
1. 与 C# delegate 行为对齐 —— 匿名 lambda 不可比是已知现状
2. 用户想"可重复取消订阅"应该保留 token 调 Dispose
3. value-equality 比较带来 hash code / Equals 协议复杂度，价值低

### Decision 4: `IDisposable` token 实现

**问题**：Subscribe 返回的 token 怎么实现？

**决定**：内部小类 `Disposable`，构造时持 cleanup `Action`，Dispose 调一次。

```z42
namespace Std;
public sealed class Disposable : IDisposable {
    private Action? _onDispose;
    public Disposable(Action onDispose) { _onDispose = onDispose; }
    public void Dispose() {
        if (_onDispose != null) {
            var fn = _onDispose;
            _onDispose = null;
            fn();
        }
    }
}
```

idempotent — 重复 Dispose 是 no-op。

### Decision 5: `IDisposable` 接口位置

stdlib 已有 `IDisposable.z42`。D2a 复用现有接口。如果接口定义不完整（没有 Dispose 方法）则补齐。

### Decision 6: continueOnException=false 在 D2a 是 fail-fast

**问题**：默认 false 路径行为？

**决定**：handler 抛出 → 异常**直接传播**到调用方（不包装 MulticastException）。

理由：与 K6 一致 —— 单 handler 多播退化为 C# delegate 的所有用法都不变。

D2a 实现：foreach loop 不 catch，让异常自然向上。

### Decision 7: continueOnException=true 在 D2a 行为

**问题**：用户写 `Invoke(arg, continueOnException: true)`，D2a 怎么处理？

**决定**：**v1 行为同 false**（fail-fast）。

理由：
1. continueOnException=true 需要 `MulticastException` 类型，那个在 D2d
2. D2a 提前 partial 实现会留下半截不一致状态
3. 行为变化在 D2d 实施时落地（与 MulticastException 一并）

记入 spec scenario：`continueOnException=true 在 D2a 暂不支持`。

## Implementation Notes

### stdlib `MulticastAction.z42`

```z42
namespace Std;

/// 多播 Action —— 一次注册多个 `Action<T>` handler，Invoke 全部触发。
/// 与单播 `Action<T>` 类型分离（K1）；调用方需求多播能力时显式选这个类型。
///
/// 内部用 `List<Action<T>>` 持订阅；Invoke 时 ToArray() 拷贝快照（COW，I14）
/// —— 触发期间 Subscribe / Unsubscribe 不影响本次触发。
///
/// D2a 仅 fail-fast 路径；continueOnException=true 在 D2d 实现。
/// ISubscription wrapper（WeakRef / OnceRef / ...）在 D2b 加。
public sealed class MulticastAction<T> {
    private List<Action<T>> _handlers = new List<Action<T>>();

    public int Count => _handlers.Count;

    public IDisposable Subscribe(Action<T> handler) {
        _handlers.Add(handler);
        return new Disposable(() => Unsubscribe(handler));
    }

    public void Unsubscribe(Action<T> handler) {
        _handlers.Remove(handler);
    }

    public void Invoke(T arg, bool continueOnException = false) {
        // COW snapshot — invoke 期间 add/remove 影响下次触发，不影响本次。
        var snapshot = _handlers.ToArray();
        foreach (var h in snapshot) {
            h(arg);
        }
    }
}
```

> `continueOnException` 参数当前未消费 —— D2d 加上聚合异常路径时启用。
> v1 用户传 true 行为同 false（fail-fast）；D2d 后变为聚合。

### `Disposable.z42`（如未存在则新增）

见 Decision 4 代码。

## Testing Strategy

### 单元测试

`src/compiler/z42.Tests/MulticastActionTests.cs`（NEW）：

1. `Empty_Bus_Count_Zero` — `new MulticastAction<int>().Count == 0`
2. `Single_Subscribe_Single_Invoke` — 一个 handler 触发一次
3. `Multiple_Handlers_FIFO_Order` — 多 handler 按订阅顺序触发
4. `Unsubscribe_Removes_Handler` — Unsubscribe 后不触发
5. `IDisposable_Token_Removes_Subscription` — token.Dispose() 等价 Unsubscribe
6. `COW_Snapshot_Mid_Invoke_Subscribe_Not_Triggered_This_Round` — 验证 COW
7. `Default_Invoke_Failfast_Propagates_Exception` — 默认抛异常直接传播

### Golden test

`src/runtime/tests/golden/run/multicast_action_basic/source.z42`：

```z42
namespace Demo;
using Std;
using Std.IO;

void Main() {
    var bus = new MulticastAction<int>();
    Console.WriteLine(bus.Count);     // 0

    bus.Subscribe((int x) => Console.WriteLine($"a:{x}"));
    bus.Subscribe((int x) => Console.WriteLine($"b:{x}"));
    Console.WriteLine(bus.Count);     // 2

    bus.Invoke(1);   // a:1 / b:1

    var token = bus.Subscribe((int x) => Console.WriteLine($"c:{x}"));
    bus.Invoke(2);   // a:2 / b:2 / c:2
    token.Dispose();
    bus.Invoke(3);   // a:3 / b:3
}
```

期望输出：
```
0
2
a:1
b:1
a:2
b:2
c:2
a:3
b:3
```

### 验证命令

```bash
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml
./scripts/build-stdlib.sh                       # MulticastAction.z42 入 stdlib
dotnet test src/compiler/z42.Tests/z42.Tests.csproj   # +7
./scripts/regen-golden-tests.sh
./scripts/test-vm.sh                            # +1×2 modes
```

## Risks & Mitigations

| 风险 | 缓解 |
|------|------|
| `List<Action<T>>` 在 generic class 内部用 generic param `T` —— stdlib 现有 generic class 是否支持 | grep 现有类（Stack<T> / LinkedList<T>）确认；如果不支持则停下讨论 |
| handler 字段类型 `Action<T>` 是 D1 注册的 stdlib 真实 delegate —— 加载顺序 | `Std.Delegates` 应在 `Std.MulticastAction` 之前编译；同 namespace 文件按文件名字母序，`Delegates.z42` < `MulticastAction.z42` ✓ |
| `IDisposable` 接口可能不存在 / 不完整 | grep 验证；缺则补加；接口很小（一个 `Dispose()` 方法） |
| stdlib `List<T>.Remove(item)` 是否走 reference equality | 验证现有 `List` 实现；不行则用 IndexOf 手动找 + RemoveAt |
| GoldenTests 加载 stdlib 时 .zbc 大小变化 | 预期，与 D1c 同步 |
| ImportedSymbols 跨包看到 MulticastAction —— TSIG 已支持 generic class（D1c 验证过） | 复用已有路径 |
