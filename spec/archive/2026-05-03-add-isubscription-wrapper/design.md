# Design: D2b — ISubscription wrapper 体系

## Architecture

```
┌── stdlib `z42.core/src/ISubscription.z42` ──────────────────┐
│  public interface ISubscription<TDelegate> {                │
│      TDelegate? TryGet();                                    │
│      bool IsAlive { get; }                                   │
│      void OnInvoked();                                       │
│  }                                                           │
└──────────────────────────────────────────────────────────────┘

┌── stdlib `z42.core/src/SubscriptionRefs.z42` ───────────────┐
│  public sealed class StrongRef<TD> : ISubscription<TD>      │
│  public sealed class OnceRef<TD>   : ISubscription<TD>      │
│  public sealed class CompositeRef<TD> : ISubscription<TD>   │
│      —— Mode flags: Once=1; Weak=2 (D2b-followup)          │
│      —— WithMode(int additional) 累加 flags 复用对象       │
│  + impl `Action<T>.AsOnce()` 等扩展方法                     │
└──────────────────────────────────────────────────────────────┘

┌── stdlib `MulticastAction<T>.z42`（D2b 修改）──────────────┐
│  Action<T>[] strong;       // fast path (D2a)              │
│  bool[]      strongAlive;                                   │
│  ISubscription<Action<T>>[] advanced;   // slow path (D2b) │
│  bool[]      advancedAlive;                                 │
│                                                              │
│  Subscribe(Action<T>)             → strong path             │
│  Subscribe(ISubscription<...>)    → advanced path           │
│                                                              │
│  Invoke:                                                     │
│    fast loop on strong:                                      │
│      if (alive[i]) strong[i](arg);                          │
│    slow loop on advanced:                                    │
│      if (alive[i]) {                                         │
│        var h = advanced[i].TryGet();                        │
│        if (h != null) h(arg);                               │
│        if (advanced[i].IsStateful) advanced[i].OnInvoked();│
│        if (!advanced[i].IsAlive) alive[i] = false;          │
│      }                                                       │
└──────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: Mode flags 用 `int` 常量（非 enum）

z42 enum 暂不支持位运算（`Mode.Once | Mode.Weak`）。v1 用 int 常量类
`ModeFlags`（`public class ModeFlags { public static int None = 0; ... }`）+ 位运算。
Future enum 升级后兼容。

### Decision 2: WeakRef + AsWeak 延后

- z42 GC heap trait 已有 `make_weak` / `upgrade_weak`，但 corelib 未暴露 builtin。
- D2b v1 不实现 WeakRef；CompositeRef.Mode 保留 `Weak=2` flag 占位（用户传入会 noop）。
- Follow-up spec `expose-weak-ref-builtin`：在 corelib 加 `__obj_make_weak` /
  `__obj_upgrade_weak`，然后在 stdlib 加 WeakRef 类 + AsWeak 扩展。

### Decision 3: Composite 融合（优化 B）的实现

`AsOnce()` impl 扩展方法定义在 ISubscription 上：

```z42
impl<TD> ISubscription<TD> {
    public ISubscription<TD> AsOnce() {
        if (this is CompositeRef<TD>) {
            return ((CompositeRef<TD>)this).WithMode(ModeFlags.Once);
        }
        return new CompositeRef<TD>(this.TryGet(), ModeFlags.Once);
    }
}
```

`WithMode` 返回 `this`（mutating）—— 复用对象。

`Action<T>.AsOnce()` impl 扩展（v1 仅 Action<T>，避免 generic delegate impl 的复杂度）：

```z42
impl<T> Action<T> {
    public CompositeRef<Action<T>> AsOnce() {
        return new CompositeRef<Action<T>>(this, ModeFlags.Once);
    }
}
```

### Decision 4: 跳过无状态 OnInvoked（优化 C）

CompositeRef 持 `Mode modes`. `OnInvoked` 实现：

```z42
public void OnInvoked() {
    if ((this.modes & ModeFlags.Once) != 0) {
        this._consumed = true;
    }
    // 无 Once / Weak / ... → 整个方法是 nop；JIT 可内联消除
}
```

但对于 StrongRef（无 mode flag），可在 invoke loop 用 `is StrongRef` 检测跳过 OnInvoked。

v1 简化：MulticastAction.Invoke slow loop 总是调 OnInvoked；OnInvoked 内部按 modes 短路。Decision 9.3 选项 C-1（mode flag 短路）是正确实现，不需要额外类型检查。

### Decision 5: ISubscription 暂无 `where TD : Delegate` 约束

z42 当前没有 `Delegate` 基类型概念（delegate 只是 Z42FuncType 别名）。所以
`ISubscription<TD>` 暂无 type-param 约束。

> 副作用：理论上 `ISubscription<int>` 也合法，但用户构造不出对应的 wrapper（StrongRef 等都假设 TD 是可调用类型）。Document as v1 limitation。

### Decision 6: `TryGet` 返回 `TD?`（nullable delegate）

`TD?` —— 当 IsAlive=false 时返回 null；caller 必须 null-check。

z42 当前是否支持 `TD?` 作为返回类型？在 generic 上下文是？依赖 z42 类型系统对
`TD?` 的处理。如果不支持就改用 default(TD) 或 throw。

> v1 验证：先按 `TD?` 写；编译失败则改方案。

### Decision 7: `IsAlive` 语义

`IsAlive` 监控订阅是否仍然活跃：
- StrongRef: 永远 true（除非显式 dispose）
- OnceRef: 触发后 false
- CompositeRef: 任一 latch flag（Once/...）触发后 false

MulticastAction.Subscribe(ISubscription) 返回的 `IDisposable` token 调用
`Dispose()` 应该让 wrapper 的 IsAlive 也变 false（通过共享一个 `bool dead`
flag 或调 wrapper 上的 dispose 方法）。v1 简化：dispose token 直接修改
MulticastAction 的 advancedAlive[i]，不动 wrapper 自身。

## Implementation Notes

### `ISubscription.z42`

```z42
namespace Std;

public interface ISubscription<TD> {
    TD? TryGet();
    bool IsAlive { get; }
    void OnInvoked();
}
```

### `SubscriptionRefs.z42`

```z42
namespace Std;

// 2026-05-03 add-isubscription-wrapper (D2b): mode flags for CompositeRef.
// v1: Once=1; Weak=2 占位（D2b-followup 实施 WeakRef 后启用）.
public class ModeFlags {
    public static int None = 0;
    public static int Once = 1;
    public static int Weak = 2;
}

public sealed class StrongRef<TD> : ISubscription<TD> {
    TD handler;
    public StrongRef(TD h) { this.handler = h; }
    public TD? TryGet() { return this.handler; }
    public bool IsAlive { get { return true; } }
    public void OnInvoked() {}
}

public sealed class OnceRef<TD> : ISubscription<TD> {
    TD handler;
    bool consumed;
    public OnceRef(TD h) {
        this.handler = h;
        this.consumed = false;
    }
    public TD? TryGet() {
        if (this.consumed) return null;
        return this.handler;
    }
    public bool IsAlive { get { return !this.consumed; } }
    public void OnInvoked() { this.consumed = true; }
}

public sealed class CompositeRef<TD> : ISubscription<TD> {
    TD handler;
    public int Modes;
    bool consumed;

    public CompositeRef(TD h, int modes) {
        this.handler = h;
        this.Modes = modes;
        this.consumed = false;
    }

    public CompositeRef<TD> WithMode(int additional) {
        this.Modes = this.Modes | additional;
        return this;
    }

    public TD? TryGet() {
        if (this.consumed) return null;
        return this.handler;
    }
    public bool IsAlive { get { return !this.consumed; } }
    public void OnInvoked() {
        if ((this.Modes & 1) != 0) {  // ModeFlags.Once
            this.consumed = true;
        }
        // Weak / 其他 mode 在 follow-up 加
    }
}
```

### impl 扩展

```z42
namespace Std;

// 2026-05-03 D2b: 让裸 Action<T> 直接 .AsOnce() 得到 CompositeRef
impl<T> Action<T> {
    public CompositeRef<Action<T>> AsOnce() {
        return new CompositeRef<Action<T>>(this, 1);  // Once
    }
}
```

> ISubscription 的 chain `.AsOnce()` 在 D2b v1 不实现（impl 扩展跨 generic interface 复杂）；用户从 Action<T> 链入 CompositeRef 后，调 `WithMode` 累加 flag。

### `MulticastAction.z42` 修改

```z42
public sealed class MulticastAction<T> {
    Action<T>[] strong;
    bool[] strongAlive;
    public int StrongSlotCount;
    int strongCapacity;

    ISubscription<Action<T>>[] advanced;
    bool[] advancedAlive;
    public int AdvancedSlotCount;
    int advancedCapacity;

    public MulticastAction() {
        this.strongCapacity = 4;
        this.strong = new Action<T>[4];
        this.strongAlive = new bool[4];
        this.StrongSlotCount = 0;

        this.advancedCapacity = 4;
        this.advanced = new ISubscription<Action<T>>[4];
        this.advancedAlive = new bool[4];
        this.AdvancedSlotCount = 0;
    }

    public IDisposable Subscribe(Action<T> handler) {
        // strong fast path
        ...
    }

    public IDisposable Subscribe(ISubscription<Action<T>> sub) {
        // advanced slow path
        ...
    }

    public int Count() { /* sum of alive in both */ }

    public void Invoke(T arg, bool continueOnException = false) {
        // Snapshot + fast loop on strong
        ...
        // Snapshot + slow loop on advanced
        ...
    }
}
```

详细见 Implementation 实施时落地。

## Testing Strategy

### 单元测试

`src/compiler/z42.Tests/SubscriptionRefsTests.cs`（NEW）：

1. `ISubscription_Interface_Resolves` — `using Std;` 后 `ISubscription<Action<int>>` 是合法类型
2. `StrongRef_IsAlive_Always_True`
3. `OnceRef_Becomes_Inactive_After_OnInvoked`
4. `CompositeRef_With_Once_Latches_Consumed`
5. `Action_AsOnce_Returns_CompositeRef` — impl 扩展可用
6. `MulticastAction_Subscribe_ISubscription_Overload`

### Golden test

`src/runtime/tests/golden/run/multicast_subscription_refs/source.z42`：

```z42
namespace Demo;
using Std;
using Std.IO;

void Main() {
    var bus = new MulticastAction<int>();

    // 强订阅 fast path
    bus.Subscribe((int x) => Console.WriteLine($"strong:{x}"));

    // 一次性订阅 slow path
    Action<int> h = (int x) => Console.WriteLine($"once:{x}");
    bus.Subscribe(h.AsOnce());

    bus.Invoke(1);     // strong:1 / once:1
    bus.Invoke(2);     // strong:2 (once 已 latch)
    bus.Invoke(3);     // strong:3
}
```

期望输出：
```
strong:1
once:1
strong:2
strong:3
```

## Risks & Mitigations

| 风险 | 缓解 |
|------|------|
| `TD?` 返回类型在 generic 上下文不被 z42 类型系统支持 | 实施时验证；不行则改 throw / use default |
| impl 块对 generic delegate type `Action<T>` 的扩展是否 work | grep 现有 stdlib impl on generic class；不行则去 impl 扩展，用静态工厂方法 `CompositeRef.OnceFor(h)` |
| ISubscription generic interface 的 `where T : Delegate` 约束缺失 → 用户错误地 `ISubscription<int>` | document v1 limitation；运行时 wrapper 构造时 fail-fast |
| 双 vec 把 D2a 的 `handlers + alive` 拆为两组，破坏 D2a 测试 | D2a golden 应当继续用 strong path；测试不变。回归运行验证 |
| ImportedSymbolLoader Phase 1.5 当前能否解析 `ISubscription<Action<T>>` 嵌套 generic | D2a 测试已覆盖类似嵌套；TSIG 路径应可行 |
