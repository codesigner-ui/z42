# Proposal: z42.core 补齐 Exception 基类 + IEnumerable/IEnumerator

## Why

当前 z42 stdlib 的 `z42.core` 在两个核心抽象上有缺口：

1. **无 Exception 基类**：`throw`/`catch` 语法已支持（L1 ✅），但 stdlib 没
   提供 `Exception` 基类或任何异常子类。用户代码只能 `throw "string"` 或
   其他任意值；stdlib / 用户无法建立统一的错误契约和层次（例如
   "捕获 ArgumentException 但让其他异常继续传播"）。
2. **无显式迭代接口契约**：foreach 有鸭子协议（L3-G4h step2：
   `int Count()` + `T get_Item(int)`），但缺 `IEnumerable<T>` /
   `IEnumerator<T>` 接口定义，导致泛型代码无法用 `where T: IEnumerable<U>`
   约束可迭代类型，也无法把集合作为"通用迭代契约"传递。

Wave 2（stdlib 重组 roadmap）补齐这两个抽象。参照 C# BCL 设计，落地为
纯 `.z42` 脚本实现（Script-First，零新增 VM extern）。

## What Changes

### Exception 层次

- `z42.core/src/Exception.z42` — `class Exception` 基类
  - 字段：`string Message`、`string? StackTrace`、`Exception? InnerException`
  - 构造器：`Exception(string msg)`、`Exception(string msg, Exception inner)`
  - 方法：覆盖 Object 协议（`ToString()` / `Equals()` / `GetHashCode()`）
- `z42.core/src/Exceptions/` 子目录 — 9 个标准派生类（仅继承 + ctor 转发）：
  - `ArgumentException`、`ArgumentNullException`、`InvalidOperationException`
  - `NullReferenceException`、`IndexOutOfRangeException`、`KeyNotFoundException`
  - `FormatException`、`NotImplementedException`、`NotSupportedException`

### Iteration 接口

- `z42.core/src/IEnumerable.z42` —
  `interface IEnumerable<T> { IEnumerator<T> GetEnumerator(); }`
- `z42.core/src/IEnumerator.z42` —
  `interface IEnumerator<T> : IDisposable { bool MoveNext(); T Current { get; } }`

### 语义不变点

- `throw` 继续接受任意 Value（L1 行为 PRESERVED，`throw "oops"` 仍合法）
- foreach 继续走索引鸭子协议（L3-G4h step2 不动），**IEnumerable 仅作显式
  接口契约**，供泛型约束和显式实现使用；未来独立变更可扩 foreach codegen
  支持 IEnumerator 路径
- 现有 List<T> / Dictionary<K,V> / Queue<T> / Stack<T> **不在本 Wave 实现
  IEnumerable**（Wave 3+，配合 KeyValuePair<K,V> 引入再批处理）

## Scope（允许改动的文件/模块）

| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `src/libraries/z42.core/src/Exception.z42` | add | 基类 |
| `src/libraries/z42.core/src/Exceptions/*.z42` | add | 9 个子类 |
| `src/libraries/z42.core/src/IEnumerable.z42` | add | 接口 |
| `src/libraries/z42.core/src/IEnumerator.z42` | add | 接口 |
| `src/libraries/z42.core/README.md` | edit | 追加 Exceptions + Iteration 章节 |
| `docs/design/exceptions.md` | add | 异常层次 + throw 语义 + stack trace 规划（使用者视角）|
| `docs/design/iteration.md` | add | IEnumerable + 鸭子协议并存 + 升级路线（使用者视角）|
| `docs/roadmap.md` | edit | L2 stdlib 条目更新 |
| `src/compiler/z42.Tests/**/run/*` | add | 每个新类型至少 1 个 golden test |

## Out of Scope

- `throw` 类型限制（保持任意 Value；未来独立变更考虑是否强制 Exception）
- foreach codegen 升级支持 IEnumerator（保留鸭子协议现状）
- 现有 List / Dictionary / Queue / Stack 实现 IEnumerable（Wave 3+）
- `KeyValuePair<K,V>`（Wave 3 配合 Dictionary.IEnumerable 引入）
- `IReadOnlyList<T>` / `ICollection<T>` 等索引派生接口（未来按需补）
- Stack trace 实际填充（本 Wave 只占 `string? StackTrace` 字段，填充延后）
- `Exception` 与 VM `PENDING_EXCEPTION` sentinel 的对接（保持任意 Value 抛
  出；Exception 只是"推荐用法"，不改 VM 异常通道）

## Open Questions

- [x] Exception 基类字段（Message/StackTrace/InnerException）— 确认完整三字段
- [x] 9 个子类挑选是否合理 — 已按 C# BCL 最常用子集
- [x] throw 是否限制 Exception 子类 — **否**，保持 L1 兼容
- [x] IEnumerator 风格 C# vs Rust — **C# 风** MoveNext + Current
- [x] foreach codegen 是否同批升级 — **否**，保持鸭子协议
- [x] List/Dictionary 是否同批实现 IEnumerable — **否**，留 Wave 3+

## Blocks / Unblocks

- **Unblocks**：
  - stdlib 后续异常抛出可用明确类型（`throw new ArgumentException("msg")`）
  - 泛型约束 `where T: IEnumerable<U>` 可用
  - Wave 3 接口补齐（IHashable / IComparer / IFormattable）可在同类基础上扩
- **Blocks**：无（L1/L2 能力充足；Script-First 零 VM 改动）
