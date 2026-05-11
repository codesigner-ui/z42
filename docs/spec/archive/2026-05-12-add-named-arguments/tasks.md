# Tasks: Named Arguments

> 状态：🟢 已完成 | 创建：2026-05-11 | 完成：2026-05-12
> 类型：lang（完整流程）
> 前置：✅ add-default-params (`2026-04-04`) 已归档；ParamModifier 体系已存在
>
> **完整落地（2026-05-12）**：AST/Parser、Z1001-Z1005 错误码、全部 10 个 call paths
> （static class / ObjNew / instance / instantiated-generic / interface / generic-param
> (base + iface) / primitive class / bareCallName / 自由顶层函数）均接入
> `BindArgsReordered`。SymbolTable 新增 `FuncDecls` 暴露顶层函数 AST，自由函数路径
> 由此读取 `Param.Name`。Codegen 端零改动 —— `BindArgsReordered` 在 TypeCheck 层
> 已用 `_boundDefaults` 填满所有中间空位，Codegen 看到的就是完整 N 个 args。
> 17 个 NamedArgumentsTests + 1228 C# 全绿 + 320 VM golden 零回归。

## 进度概览

- [x] 阶段 1: AST + Parser
- [x] 阶段 2 Part 1: TypeCheck shim + 错误码注册 + Z1001 wired
- [x] 阶段 2 Part 2: 实际 reorder + Z1002–Z1005 触发（10 个 call paths）
- [x] 阶段 3: Codegen FillDefaults 升级 —— **不需要**（TypeCheck 层已填满空位）
- [x] 阶段 4: 测试 + 文档

## 阶段 1: AST + Parser

- [x] 1.1 [Ast.cs](../../../../src/compiler/z42.Syntax/Parser/Ast.cs) — `Argument(string? Name, Expr Value, Span, Span? NameSpan)` record（设计微调：Modifier/OutVar 保留在 Value 内的 `ModifiedArg` 包装中，避免 ModifiedArg 全网删除）
- [x] 1.2 同上 — `CallExpr.Args` 与 `NewExpr.Args` 类型升级 `List<Expr>` → `List<Argument>`
- [x] 1.3 [ExprParser.Atoms.cs](../../../../src/compiler/z42.Syntax/Parser/ExprParser.Atoms.cs) — `ParseCallArgumentWithOptionalNameAndModifier` lookahead `IDENT :` → 命中即 wrap 为 `Argument(Name, ...)`；ternary `a ? b : c` 不命中（`?` 在 inner 表达式后）
- [x] 1.4 同上 — 不命中分支 wrap 为 `Argument(Name: null, ...)`
- [x] 1.5 同上 — `out var x` 分支：modifier 检测在 name lookahead 之后，OutVarDecl 仍 attached 到 ModifiedArg
- [x] 1.6 [TopLevelParser.Members.cs](../../../../src/compiler/z42.Syntax/Parser/TopLevelParser.Members.cs) — event/delegate synthesized CallExpr/NewExpr 改 `new List<Argument>`（~7 处）
- [x] 1.7 [AstDumper.cs](../../../../src/compiler/z42.Pipeline/AstDumper.cs) — `VisitArgument` 节点显示
- [x] 1.8 [ParserTests.cs](../../../../src/compiler/z42.Tests/ParserTests.cs) — 5 个 ArgModifier 测试更新（`call.Args[i]` 现在是 Argument，断言访问 `.Value`）

## 阶段 2: TypeCheck binding + 错误码

- [x] 2.1 [Diagnostic.cs + DiagnosticCatalog.cs](../../../../src/compiler/z42.Core/Diagnostics/) — 5 个错误码 Z1001–Z1005 注册 + catalog 文档
- [x] 2.2 [TypeChecker.Calls.Modifiers.cs](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.Modifiers.cs) — 工具函数：
  - 2.2a `CheckPositionalBeforeNamed` → Z1001 wired
  - 2.2b `BindArgValue(Argument, env)` — bind shim（位置形态下 Name 静默丢弃）
  - 2.2c `ResolveArgPositions(args, paramByName)` 已实现但尚未在 call paths 调用
  - 2.2d `ReorderToPositional(resolved, paramCount, env)` 已实现但尚未在 call paths 调用
- [x] 2.3 [TypeChecker.Calls.cs](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs) — `BindCall` 入口加 `CheckPositionalBeforeNamed(call.Args)`；5 处 `call.Args.Select(a => BindExpr)` 改为 `BindArgValue`
- [x] 2.4 [TypeChecker.Calls.Overload.cs](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.Overload.cs) — `LookupMethodOverload` / `CheckArgTypes` 签名 `IReadOnlyList<Expr>` → `IReadOnlyList<Argument>`
- [x] 2.5 [TypeChecker.Calls.Helpers.cs](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.Helpers.cs) — `BindAndCheckArgs` 签名升级
- [x] 2.6 [TypeChecker.Calls.Modifiers.cs](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.Modifiers.cs) — `CheckArgModifiers` 签名升级
- [x] 2.7 [Z42Type.cs](../../../../src/compiler/z42.Semantics/TypeCheck/Z42Type.cs) — `ModifierMangling.PatternFromArgs` / `HasAnyModifier` 签名升级
- [x] 2.8 [TypeChecker.Exprs.cs](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs) — `NewExpr` 分支 `CheckPositionalBeforeNamed` + `BindArgValue`
- [x] 2.9 **Part 2 (2026-05-12)**：`BindArgsReordered` 统一 helper + 静态方法 / ObjNew 两个核心路径接入；Z1002 / Z1003 / Z1004 / Z1005 实时触发；BoundDefaults 通过 `_boundDefaults` 字典访问 + 按需补绑
- [x] 2.10 **Part 2**：imported / 跨 CU callees 当 `Decl == null` 时走 Z1002 fallback（保留 forward progress；下游类型校验仍执行）
- [x] 2.11 **Part 2 (2026-05-12)**：扩展接入 9 个 call paths — static class / ObjNew(ctor) / Z42InstantiatedType / Z42ClassType instance / Z42InterfaceType / Z42GenericParamType (base class + interface) / primitive class / bareCallName（curCt.StaticMethods）；所有 IMethodSymbol-based 路径均通过 `Decl?.Params` 接入 reorder
- [x] 2.12 **Part 2 (2026-05-12)**：自由顶层函数路径接入 —— `SymbolCollector` 维护 `_funcDecls: Dictionary<string, FunctionDecl>`；`SymbolTable.FuncDecls` 暴露；BindCall 自由函数分支 `_symbols.FuncDecls.TryGetValue(resolvedFuncName, out var fnDecl)` 取得 Decl 后接入 `BindArgsReordered`。Imported / 局部嵌套函数仍走 Z1002 fallback（无 decl 记录）

## 阶段 3: Codegen FillDefaults 升级 — **不需要（2026-05-12 验证）**

`BindArgsReordered` 在 TypeCheck 层已经把中间空位用 `_boundDefaults[Param]`（或新 bind 的 default expr）填满，输出长度始终等于 `calleeParams.Count`。Codegen 接收的 `BoundCall.Args` 已是完整 N 个 args，**不会**触发 `FillDefaults` 的"补尾部"路径。

- [x] 3.1 验证：`BindArgsReordered` 输出 = N 个 BoundExpr（无 null 空位） — 见 [TypeChecker.Calls.Modifiers.cs L169-198](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.Modifiers.cs)
- [x] 3.2 Codegen `FillDefaults` 仅在 "no named args + 尾部省略" 时进入 — 行为与现状一致，无需改动
- [x] 3.3 17 个 NamedArgumentsTests 验证 IR/VM 端到端语义正确

## 阶段 4: 测试 + 文档

- [x] 4.1 [NamedArgumentsTests.cs](../../../../src/compiler/z42.Tests/NamedArgumentsTests.cs) NEW — 17 个 case 全绿（Parser 3 + Z1001-Z1005 共 5 + reorder 4 + 2 instance method + 3 free function；2026-05-12）
- [ ] 4.2 [src/tests/calls/named_args/](../../../../src/tests/calls/named_args/) NEW — golden e2e（推迟；17 个单测已覆盖 IR/VM 端到端；现有 stdlib 不依赖此特性）
- [x] 4.3 既有 1228 C# + 320 VM golden 全绿 → ternary `a ? b : c` 未被新 lookahead 误伤
- [x] 4.4 [docs/design/language/language-overview.md](../../../../docs/design/language/language-overview.md) §5 — 在"默认参数"段后追加具名实参语法示例 + 5 个 Z10xx 错误示例（2026-05-12）
- [x] 4.5 [examples/named_args.z42](../../../../examples/named_args.z42) NEW — 演示静态方法 / ctor / 自由函数 + 跳过中间默认（2026-05-12）
- [x] 4.6 全绿验证：1228 C# + 309 Rust unit + 320 VM golden（2026-05-12 完整接入）
