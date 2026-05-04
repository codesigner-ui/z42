# Tasks: 单播 event IDisposable token + 严格 access control（D-7-residual）

> 状态：🟢 已完成 | 创建：2026-05-04 | 完成：2026-05-04
>
> **实施期间发现**：原 Decision 1 选项 B（嵌套 sealed token 类）依赖 z42 未实现的嵌套 class 基础设施。切换到选项 A：新增 stdlib `Std.Disposable : IDisposable` + `Disposable.From(Action)` 工厂，单播 add_X body `return Disposable.From(() => this.remove_X(h))` 通过 lambda 捕获 this+h。Decision 调整在归档前已通过 stop-and-ask 报告 User。

## 进度概览
- [ ] 阶段 1: 基础（diagnostic 码 + EventFieldNames 查询确认）
- [ ] 阶段 2: 单播 IDisposable token 合成
- [ ] 阶段 3: E0414 access control
- [ ] 阶段 4: 测试 + 文档

## 阶段 1: 基础
- [ ] 1.1 `src/compiler/z42.Core/Diagnostics/Diagnostic.cs` 加常量 `EventFieldExternalAccess = "E0414"`
- [ ] 1.2 确认 `Z42ClassType.EventFieldNames` 在 SymbolCollector 已收集（exploration 已验，复核）

## 阶段 2: 单播 IDisposable token 合成
- [ ] 2.1 `TopLevelParser.Members.cs` 单播路径合成嵌套 sealed class `Disposable_{FieldName}`：字段 `owner / handler`，构造器，Dispose 方法（调用 owner.remove_X 并比较 reference equality）
- [ ] 2.2 同文件单播 `add_X` 改返回类型 `IDisposable`，body 末尾 `return new Disposable_{FieldName}(this, h)`
- [ ] 2.3 token 类标 `Visibility.Private`（不暴露给用户代码）
- [ ] 2.4 跑 `event_keyword_singlecast` golden（D-7 主体测试）确认未回归 single-binding throw 行为

## 阶段 3: E0414 access control
- [ ] 3.1 `TypeChecker.Exprs.Members.cs` FieldAccess 加 EventFieldNames 检查；外部访问标记 `IsExternalEventAccess`
- [ ] 3.2 `TypeChecker.Exprs.Calls.cs`（或 callee 处理位置）若 callee 是外部 event field → 报 E0414
- [ ] 3.3 `TypeChecker.Stmts.cs` AssignStmt LHS 是外部 event field → 报 E0414
- [ ] 3.4 verify `+=` / `-=` desugar 已转 add_X / remove_X static call，不会误报

## 阶段 4: 测试 + 文档
- [ ] 4.1 NEW `src/compiler/z42.Tests/Diagnostics/EventAccessControlTests.cs`：单播外部 invoke ❌、单播外部 assign ❌、多播外部 invoke ❌、多播外部 assign ❌、单播内部 invoke ✅、外部 += / -= ✅
- [ ] 4.2 NEW `src/runtime/tests/golden/run/event_singlecast_idisposable/source.z42` + `expected_output.txt`：var t = ev.Sub += h; ev.Fire(); t.Dispose(); ev.Fire(); 应只 fire 一次
- [ ] 4.3 `docs/design/delegates-events.md` §6.3 / §6.5 IDisposable + access control 标记落地，status 行追加 D-7-residual
- [ ] 4.4 `docs/deferred.md` 移除 D-7-residual 条目
- [ ] 4.5 `docs/roadmap.md` event 行进度表更新

## 阶段 5: 验证
- [ ] 5.1 `dotnet build src/compiler/z42.slnx && cargo build --manifest-path src/runtime/Cargo.toml` —— 无编译错误
- [ ] 5.2 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` —— 全绿（包括新加的 EventAccessControlTests）
- [ ] 5.3 `./scripts/test-vm.sh` —— 全绿（包括新 golden + 现有 event/multicast 全过）
- [ ] 5.4 spec scenarios 5 个场景逐条对应 ✅
- [ ] 5.5 文档同步：`docs/design/delegates-events.md`、`docs/deferred.md`、`docs/roadmap.md`

## 备注
- E0414 占用 type-checker 段下一个空闲号
- 单播 token 用嵌套类 + 嵌套类标 private（Decision 1, 4）
- access control 一并修单播 + 多播（Decision 3）
