# Proposal: D2c-多播 — `event` 关键字 + 多播 `+=` / `-=` desugar

## Why

[delegates-events.md §6](docs/design/delegates-events.md#6) 设计的 `event` 关键字消灭外部 `event?.Invoke(...)` 模板代码：多播 event 字段 default-init 为空 MulticastAction（invoke on empty = no-op），外部 `+=` / `-=` 仅限调用合成的 `add_X` / `remove_X`。

D2a (MulticastAction) + D2b (ISubscription wrapper) + D-5 (delegate equality + Unsubscribe) 已 GREEN，本 spec 不再有前置阻塞。

**本 spec 范围 = 多播 event 主路径**：

- `event` 关键字 lexer/parser/AST 支持（同时铺平单播路径）
- 多播 event（`event MulticastAction<T> X;`）auto-init + 合成 `add_X` / `remove_X`
- `obj.X += h` / `obj.X -= h` desugar 到 `obj.add_X(h)` / `obj.remove_X(h)`
- 多播 event field 字段保持 public 访问（与 D2a/b 现状一致；严格写访问控制延后到 2b）

**out of scope（移到 Spec 2b `add-event-keyword-singlecast`）**：

- 单播 event（`event Action<T>` / `event Func<T,R>` / `event Predicate<T>`）的 throw on double-bind + IDisposable 清空 lambda 语义
- 严格 access control（外部 `obj.X.Invoke()` / `obj.X = ...` 报错）
- `Std.Disposable.From(Action)` 工厂

## What Changes

- **Lexer**：`event` 关键字（`TokenKind.Event` + `Keywords` 注册）
- **Ast**：`FieldDecl.IsEvent: bool`
- **Parser**：accept `event` modifier 在 vis + 非 vis modifiers 之后、type 之前
- **SymbolCollector / TypeChecker**：
  - 仅多播 event 路径：验证字段类型必须是 `MulticastAction<T>`（单播留 2b）
  - 多播 event field 自动 default-init 为 `new MulticastAction<T>()`（复用 D2a/b auto-init 路径）
  - 在 class 上合成 `add_<X>(Action<T>): IDisposable` 与 `remove_<X>(Action<T>)` 方法（同 SynthesizeClassAutoProp 模式直接 emit AST 节点）
- **TypeChecker**：`obj.X += h` / `obj.X -= h` 在 LHS 是 event field 时 desugar 到对应 add/remove 方法调用

非多播 event 字段类型在本 spec 报错 "single-cast event not yet supported (D2c-singlecast pending)" —— 保留 keyword 解析能力，留待 2b 落地。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Lexer/TokenKind.cs` | MODIFY | 加 `Event` |
| `src/compiler/z42.Syntax/Lexer/TokenDefs.cs` | MODIFY | `Keywords` 注册 `"event"` → `Event` |
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | `FieldDecl` 加 `IsEvent: bool` 末尾参数 |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Types.cs` | MODIFY | parse `event` modifier；构造 FieldDecl 时透传 IsEvent；多播 event 同时 synthesize add/remove FunctionDecl |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | MODIFY | `IsFieldDecl` lookahead 含 `event` token |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Operators.cs` | MODIFY | BindCompoundAssign 检测 LHS 是 event field 时 desugar 到 add/remove |
| `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` | MODIFY | `Z42ClassType` 加 `EventFieldNames` 集合元数据（用于 BindAssign 检测） |
| `src/compiler/z42.Tests/EventKeywordTests.cs` | NEW | 单元测试 |
| `src/runtime/tests/golden/run/event_keyword_multicast/source.z42` | NEW | 端到端 golden |
| `src/runtime/tests/golden/run/event_keyword_multicast/expected_output.txt` | NEW | 预期输出 |

**只读引用**：

- `src/compiler/z42.Syntax/Parser/TopLevelParser.Members.cs` — `SynthesizeClassAutoProp` 复用模式
- `src/libraries/z42.core/src/MulticastAction.z42` — 多播 event 委托对象
- `src/libraries/z42.core/src/IDisposable.z42` — 接口 (Subscribe 返回类型)

## Out of Scope (→ 2b)

- 单播 event 语义 (`event Action<T>` 字段)
- `Std.Disposable.From(Action)` 工厂
- 严格 access control（外部 invoke / 直接 set 报错）
- interface event default 实现（→ Spec 3）
- D2d MulticastFunc / MulticastPredicate event 支持

## Open Questions

- [ ] add_X 实际返回 IDisposable 即 MulticastSubscription —— 调用方丢弃；TypeChecker `+=` desugar emit BoundCall 但 result 在 statement 上下文如何丢弃？倾向 BoundExprStmt 包裹
