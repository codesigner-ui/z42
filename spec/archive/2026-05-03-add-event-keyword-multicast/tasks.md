# Tasks: D2c-多播 — `event` 关键字 + 多播 `+=` / `-=` desugar

> 状态：🟢 已完成 | 创建：2026-05-03 | 完成：2026-05-03 | 类型：lang/parser/typechecker（完整流程）
> **依赖**：D2a + D2b + D-5 GREEN

## 进度概览
- [x] 阶段 1: lexer + AST + parser
- [x] 阶段 2: parser 合成多播 event field auto-init + add/remove 方法
- [x] 阶段 3: TypeChecker `+=` / `-=` desugar + Z42ClassType.EventFieldNames
- [x] 阶段 4: 测试
- [x] 阶段 5: 验证 + 文档同步 + 归档

## 阶段 1: Lexer + AST + Parser
- [x] 1.1 `TokenKind.cs` 加 `Event`
- [x] 1.2 `TokenDefs.cs` `Keywords` 注册 `event` → `Event`
- [x] 1.3 `Ast.cs` `FieldDecl` 加 `IsEvent` 末尾参数（默认 false）
- [x] 1.4 `TopLevelParser.Types.cs` parse `event` modifier 在 vis + 非 vis modifiers 之后；FieldDecl 透传 IsEvent；event 与 auto-property 互斥校验
- [x] 1.5 `TopLevelParser.Helpers.cs` `IsFieldDecl` lookahead skip `event` token

## 阶段 2: Parser 合成多播 event field auto-init + add/remove
- [x] 2.1 `TopLevelParser.Members.cs` 加 `SynthesizeClassEvent(FieldDecl)` 同 SynthesizeClassAutoProp 模式：
  - 验证字段类型 = `GenericType("MulticastAction", [T])`，否则报 "single-cast event not yet supported"
  - 合成 default-init expr `new MulticastAction<T>()`
  - 合成 `add_<X>(Action<T>): IDisposable`：body `return this.X.Subscribe(h);`
  - 合成 `remove_<X>(Action<T>): void`：body `this.X.Unsubscribe(h);`
- [x] 2.2 `TopLevelParser.Types.cs` 在 IsFieldDecl 路径触发 SynthesizeClassEvent（fields 加 backing，methods 加 accessors）

## 阶段 3: TypeChecker desugar + EventFieldNames
- [x] 3.1 `Z42Type.cs` `Z42ClassType` 加 `IReadOnlySet<string>? EventFieldNames`
- [x] 3.2 `SymbolCollector.Classes.cs` 收集 IsEvent fields 名加入 EventFieldNames
- [x] 3.3 `TypeChecker.Exprs.Operators.cs` BindAssign 入口检测 LHS MemberExpr + 字段在 EventFieldNames + RHS 是 BinaryExpr `+`/`-`：emit `BoundCall(target, add_<X>/remove_<X>, [rhs])`

## 阶段 4: 测试
- [x] 4.1 NEW `src/compiler/z42.Tests/EventKeywordTests.cs` 5 scenarios（parser 合成 + 单播 not-yet-supported + auto-property 互斥）
- [x] 4.2 NEW `src/runtime/tests/golden/run/event_keyword_multicast/source.z42` 端到端 `+=` / `-=` + 内部 Invoke
- [x] 4.3 NEW `expected_output.txt`
- [x] 4.4 `./scripts/regen-golden-tests.sh` 120 ok

## 阶段 5: 验证 + 文档 + 归档
- [x] 5.1 `dotnet build` ✅
- [x] 5.2 `dotnet test` 959/959 ✅（基线 953 + 5 EventKeywordTests + 1 golden 自发现）
- [x] 5.3 `./scripts/test-vm.sh` 236/236 ✅（基线 234 + interp/jit 各 1）
- [x] 5.4 `./scripts/build-stdlib.sh` 6/6 ✅
- [x] 5.5 spec scenarios 逐条核对
- [x] 5.6 文档同步：
  - `docs/design/delegates-events.md` 顶部状态加 D-5 + D2c-多播
  - `docs/roadmap.md` 历史表加一行
  - `docs/deferred-features.md` D-7 改写：多播已落地，单播留 2b
- [x] 5.7 移动 `spec/changes/add-event-keyword-multicast/` → `spec/archive/2026-05-03-add-event-keyword-multicast/`
- [x] 5.8 commit + push

## 备注
- 单播 event 改报"not yet supported"，留 Spec 2b（独立 `add-event-keyword-singlecast`）
- 严格 access control（外部 invoke / 直接 set 报错）也留 2b
- interface event default 留 Spec 3
- 实施期间 Spec 2 切分：原 spec `add-event-keyword` 拆为 `add-event-keyword-multicast`（本 spec）+ `add-event-keyword-singlecast`（待立）
- EventKeywordTests 单元测试无 stdlib 上下文，`+=` / `-=` desugar 端到端验证由 golden 覆盖
