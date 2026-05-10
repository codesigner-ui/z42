# Design: Exception hierarchy + IEnumerable/IEnumerator

## Architecture

```
z42.core/src/
├── Exception.z42                        ← 基类 (Message/StackTrace/InnerException)
├── Exceptions/
│   ├── ArgumentException.z42
│   ├── ArgumentNullException.z42        : ArgumentException
│   ├── InvalidOperationException.z42
│   ├── NullReferenceException.z42
│   ├── IndexOutOfRangeException.z42
│   ├── KeyNotFoundException.z42
│   ├── FormatException.z42
│   ├── NotImplementedException.z42
│   └── NotSupportedException.z42
├── IEnumerable.z42                      ← interface IEnumerable<T>
└── IEnumerator.z42                      ← interface IEnumerator<T> : IDisposable
```

依赖方向（都在 z42.core 包内，namespace `Std`）：

```
Exceptions/* ──► Exception ──► Object
IEnumerator<T> ──► IDisposable (已有)
IEnumerable<T> ──► IEnumerator<T>
```

无跨包依赖；无 VM 改动。

## Decisions

### Decision 1: Exception 基类三字段设计

**问题**：`Exception` 需要哪些字段？

**决定**：`Message` + `StackTrace` + `InnerException` 三字段。

```z42
namespace Std;

public class Exception {
    public string Message;
    public string? StackTrace;
    public Exception? InnerException;

    public Exception(string message) {
        this.Message = message;
        this.StackTrace = null;
        this.InnerException = null;
    }

    public Exception(string message, Exception inner) {
        this.Message = message;
        this.StackTrace = null;
        this.InnerException = inner;
    }

    public override string ToString() {
        return "Exception: " + this.Message;
    }
}
```

**理由**：C# BCL 对齐；StackTrace 字段暂时为 null（Phase 1 VM 不填充），
但占位让未来 stack trace 机制接入时不改接口。InnerException 支持 exception
wrapping（C# `try { ... } catch (Exception inner) { throw new Custom(msg, inner); }`）。

### Decision 2: 子类仅继承 + ctor 转发

**问题**：9 个子类是否需要独立字段或方法？

**决定**：**仅继承 + 构造器转发**，不新加字段。ToString 可选覆盖为
`"<ClassName>: <Message>"`（每个子类硬编码类名字符串，不依赖反射）。

```z42
namespace Std;

public class ArgumentException : Exception {
    public ArgumentException(string message) : base(message) {}
    public ArgumentException(string message, Exception inner) : base(message, inner) {}
    public override string ToString() {
        return "ArgumentException: " + this.Message;
    }
}
```

**理由**：
- C# BCL 的子类大多也仅是继承标记，个别加 `ParamName` 等字段（本 Wave 不上）
- 不依赖反射（`typeof` / `GetType` 运行时类名）简化实现 —— Phase 1 的
  `GetType().Name` 是否稳定需单独验证，先用硬编码字符串稳妥

### Decision 3: IEnumerable/IEnumerator 接口形状（C# 风）

**问题**：迭代器接口用 C# 风 `MoveNext + Current` 还是 Rust 风 `Next -> Option<T>`？

**决定**：**C# 风**。

```z42
namespace Std;

public interface IEnumerator<T> : IDisposable {
    bool MoveNext();
    T Current { get; }
}

public interface IEnumerable<T> {
    IEnumerator<T> GetEnumerator();
}
```

**理由**：
- z42 整体对齐 C# BCL 命名（见 `docs/design/stdlib.md` Design Philosophy）
- `Option<T>` 尚未引入（L3 ADT），Rust 风需等 ADT 可用
- `MoveNext() → Current` 两阶段访问更适合手写 iterator（避免 Option wrap/unwrap 热路径）

### Decision 4: property 语法兼容性兜底

**问题**：接口内 `T Current { get; }` property 语法是否已被 parser 支持？

**决定**：接口层面 property 已通过 L3-G4e 索引器语法支持（`T this[int] { get; set; }`）；
普通 property 需要确认 `InterfaceDef` 的方法列表支持 property 声明。

**兜底方案**：若 parser / TypeCheck 对接口 property 声明支持不完整，
退化为方法形式 `T Current()`。实施阶段 2.3 探测后决定。

**理由**：property 是 C# 风最自然写法；但本 Wave 不做 parser 改动，
若兼容性缺口即降级，不阻塞 Wave 主目标。

### Decision 5: foreach codegen 不变，IEnumerable 仅作契约

**问题**：是否让 foreach 识别 IEnumerable 路径？

**决定**：**不动**。foreach 继续走 L3-G4h step2 的索引鸭子协议
（Count + get_Item）。IEnumerable<T> 只作为：
- 显式接口声明（`class List<T> : IEnumerable<T>` — 但本 Wave 不实现）
- 泛型约束契约（`where T: IEnumerable<U>`）
- 未来 LINQ / iterator chain 的协议层入口

**理由**：
- 最小侵入，foreach 路径零改动；现有 80+ golden 测试零风险
- 升级 foreach 识别 IEnumerator 是独立工作（需考虑鸭子协议与接口路径的
  优先级、Dispose 自动调用、break/continue 清理等），留未来 change
- Wave 2 聚焦"接口定义"，不扩 codegen

### Decision 6: 子类独立文件 vs 合并一个文件

**问题**：9 个子类每个一个文件（简单继承）是否 churn 过多？

**决定**：**每个子类独立文件**，放 `z42.core/src/Exceptions/` 子目录。

**理由**：
- 与 `Collections/` 子目录形式一致
- 未来补 `ParamName` / `ErrorCode` 等字段时不影响其他子类 git 历史
- sources 递归通配 `src/**/*.z42`，无需改 manifest

### Decision 7: stdlib build 先行测试

**问题**：9 + 2 新类型能否被 stdlib build 成功编译？

**决定**：实施阶段 1 完成 Exception 基类后立即跑 `build-stdlib.sh` 验证
一次，再加子类；发现 parser / TypeCheck 缺口立即报告。

**理由**：防止堆积 11 个新文件后才发现基础类无法编译。

## Implementation Notes

### Exception 的 override ToString 形式

```z42
public override string ToString() {
    return "ArgumentException: " + this.Message;
}
```

**注意**：硬编码类名字符串 vs 运行时查类名。Phase 1 **硬编码**（9 个子类
重复一行），避免依赖 `GetType().Name` 运行时行为；L3 反射（L3-R）稳定后
可重构为基类统一 `return GetType().Name + ": " + Message;`。

### 可空性

- `StackTrace` 类型：`string?` — 当前 Phase 1 全部为 null
- `InnerException` 类型：`Exception?` — 无 inner 时为 null

L1 可空类型 `T?`（隐式包装）已支持（roadmap 确认）。

### IDisposable 继承

`IEnumerator<T> : IDisposable` 要求实现者提供 `void Dispose()`。本 Wave
的 IEnumerator 暂无实际实现（没人 GetEnumerator()），不产生运行时调用。
Wave 3+ 让 List 实现 IEnumerable 时，对应 IEnumerator 实现需提供
empty `Dispose() {}`。

### 接口 property 探测

阶段 2.3（IEnumerator.z42 编写）时先写 property 形式：

```z42
public interface IEnumerator<T> : IDisposable {
    bool MoveNext();
    T Current { get; }
}
```

跑 stdlib build 若报 parser 错，降级为：

```z42
public interface IEnumerator<T> : IDisposable {
    bool MoveNext();
    T GetCurrent();
}
```

并在 tasks.md 备注；不阻塞 Wave。

## Testing Strategy

### Golden tests（新增）

每个新增类型至少 1 个 golden test：

1. `run/90_exception_base` — Exception 构造 + Message / StackTrace /
   InnerException 访问 + ToString
2. `run/91_exception_subclass` — ArgumentException / InvalidOperationException
   构造 + throw/catch + is-check
3. `run/92_exception_inner_chain` — 嵌套 Exception wrap / unwrap
4. `run/93_ienumerable_contract` — 自定义 class 实现 IEnumerable<int>，
   编译 + IEnumerator 调用验证（无需 foreach，验证接口契约）
5. `run/94_ienumerable_generic_constraint` — `T Count<T>(IEnumerable<T> xs)`
   泛型约束路径

若 property 语法降级，测试 5 改用 `GetCurrent()` 方法形式。

### 回归覆盖

- `run/12_exceptions` / `run/41_try_finally` 等使用 `throw "string"` 的
  现有 golden — 必须保持绿（验证 Phase 1 任意值 throw 不破）
- `run/80_stdlib_arraylist` / `83_foreach_user_class` — 验证 foreach 不破
- 全量 `test-vm.sh` + `dotnet test` + `cargo test` 必须全绿

## 兼容性风险

| 风险 | 评估 | 缓解 |
|------|------|------|
| 接口 property 语法未完全支持 | 中 | Decision 4 兜底降级为方法形式 |
| Exception 类名硬编码 ToString 误差 | 低 | 9 个子类都显式写；L3-R 后可统一 |
| override 关键字在 stdlib class 未被接受 | 低 | 已在 stdlib 其他类广泛使用（String/Int 的 Object 协议 override）|
| `InnerException?` 可空类型 propagation | 低 | L1 `T?` 已 ✅ |
| stdlib build 因类间依赖 cycle 失败 | 低 | 依赖图是 DAG（base → subclass）|
