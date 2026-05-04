# Proposal: 单播 event IDisposable token + 严格 access control（D-7-residual）

## Why

D-7（`add-event-keyword-singlecast`，2026-05-04）落地了单播 `event` 关键字，但留两块未做：

1. **单播 `add_X` 返回 void**（设计 `delegates-events.md` §6.3 line 301 是 `IDisposable`）。多播 `add_X` 已返回 `IDisposable`（基于 `MulticastSubscription<T>`），单播这边断了对称性，用户无法 `using (var t = btn.OnKey += h) { ... }` 自动 cleanup。
2. **外部直接 invoke / 直接赋值 event field 不报错**：`obj.X.Invoke(...)` 和 `obj.X = newAction` 都被允许（多播单播都这样），破坏 event 封装语义 —— event 字段本意是外部只能 `+=`/`-=`，不能直接 invoke 或替换。设计 §6.5 明确"严格 access control"。

不做会导致：① IDisposable cleanup 体验只在多播半边，单播是裸 `-=` 调用易忘；② event 封装失效，外部代码可以绕过 add/remove 直接写字段，event 与普通 delegate 字段实际上没区别。

## What Changes

1. **单播 `add_X` 返回 IDisposable**：每个单播 event 编译期合成一个私有 `Disposable_{FieldName}` token 类（嵌套 sealed class），`add_X` 创建实例返回；`Dispose()` 调用对应的 `remove_X`
2. **严格 access control 检查**：TypeChecker 在外部访问 event field 时检测 `obj.X.Invoke(...)` 和 `obj.X = ...`，报新分配的 **E0414 EventFieldExternalAccess**
3. **新 diagnostic 码 E0414** 加入 `DiagnosticCodes.cs`
4. 多播 + 单播两条路径都接入 E0414（两者 access control 缺失对称）
5. golden test 覆盖：IDisposable 自动 cleanup + E0414 触发

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Core/Diagnostics/Diagnostic.cs` | MODIFY | 加 `EventFieldExternalAccess = "E0414"` 常量 |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Members.cs` | MODIFY | 单播 event 合成代码：`add_X` 返回类型 `IDisposable`；同时合成嵌套 `Disposable_{FieldName}` token 类（实现 `IDisposable.Dispose()` 调用 `remove_X`） |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Members.cs` | MODIFY | FieldAccess 检测：若字段是 event 且不在拥有类内部，报 E0414 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Stmts.cs` | MODIFY | Assign 检测：左侧是外部 event field，报 E0414（若已在 Stmts.cs 处理，否则相应文件） |
| `src/compiler/z42.Tests/Diagnostics/EventAccessControlTests.cs` | NEW | E0414 单元测试：① 外部 `obj.X.Invoke()` ② 外部 `obj.X = a`（多播 + 单播两类） |
| `src/runtime/tests/golden/run/event_singlecast_idisposable/source.z42` | NEW | golden：`var t = ev.Sub += h; t.Dispose();` 验证 cleanup |
| `src/runtime/tests/golden/run/event_singlecast_idisposable/expected_output.txt` | NEW | golden 期望输出 |
| `docs/design/delegates-events.md` | MODIFY | §6.3 IDisposable 段把"待 D-7-residual"标记改为"已落地"；§6.5 同款；status 行追加 D-7-residual |
| `docs/deferred.md` | MODIFY | 移除 D-7-residual 条目 |
| `docs/roadmap.md` | MODIFY | event 行进度表加 IDisposable / access control 列状态 |

**只读引用**：
- `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs:127-140` — `EventFieldNames` HashSet 已收集，TypeChecker 直接查
- `src/compiler/z42.Syntax/Parser/TopLevelParser.Members.cs:237-282` — 多播 add_X synthesis 模式（参考做单播 token 类）
- `src/libraries/z42.core/src/IDisposable.z42` — IDisposable interface 定义
- `spec/archive/2026-05-04-add-event-keyword-singlecast/` — D-7 主体 spec 历史

## Out of Scope

- E0414 之外的 event-related diagnostic 编码（如 try-catch event field 等场景）
- 单播 event 改成 `event Func<T>` 形态（设计上单播 event 限定 `Action<T>` 类，不在本变更）
- 多播 event 单独的 token 类型（多播已有 `MulticastSubscription<T>`，复用即可）

## Open Questions

无（IDisposable 合成模式参照多播 add_X，access control 触发条件已在设计 §6.5 明确）
