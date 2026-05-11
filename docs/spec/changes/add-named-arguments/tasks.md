# Tasks: Named Arguments

> 状态：🟡 进行中（Part 1 已落地）| 创建：2026-05-11
> 类型：lang（完整流程）
> 前置：✅ add-default-params (`2026-04-04`) 已归档；ParamModifier 体系已存在
>
> **Part 1（已落地，build 全绿）**：AST/Parser 完整支持 named-arg 形态；
> TypeCheck 走 shim（Name 字段被静默丢弃，等价于位置实参 — 不影响现有 1211 测试）；
> Z1001（positional-after-named）在 BindCall 入口实时触发；5 个 Z10xx 错误码注册 + catalog。
>
> **Part 2（待跟进；single-session 不足）**：实际名字-位置 reorder（要触 15+ TypeChecker
> call paths）；BoundDefaults 填中间空位；imported callee 的 param-name 来源（需扩
> `Z42FuncType.ParamNames` 字段或路径定制）；14 个单测 + golden e2e + 文档同步。

## 进度概览

- [x] 阶段 1: AST + Parser
- [x] 阶段 2 Part 1: TypeCheck shim + 错误码注册 + Z1001 wired
- [ ] 阶段 2 Part 2: 实际 reorder + Z1002–Z1005 触发
- [ ] 阶段 3: Codegen FillDefaults 升级（仅在 Part 2 真正需要时；当前 shim 不动 Codegen）
- [ ] 阶段 4: 测试 + 文档

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
- [ ] 2.12 跟进：自由函数 `env.LookupFunc(name)` 路径（line ~382）仍走 shim — 仅当引入 `Z42FuncType.ParamNames` 字段后接入；当前命中此路径的 named-arg 等价位置实参（无错误，但无 reorder）

## 阶段 3: Codegen FillDefaults 升级

- [ ] 3.1 [FunctionEmitterCalls.cs](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs) — 接受 `List<BoundExpr?>` 输入；按 param 顺序迭代，非 null 走 EmitExpr，null 走 FillDefaults / EmitTypeDefault
- [ ] 3.2 同上 — `FillDefaults` 签名/语义升级；旧"补尾部"路径覆盖到"中间空位"
- [ ] 3.3 同上 — VCall / ObjNew / CallNative 各 path 走统一空位填补
- [ ] 3.4 [BoundExprVisitor.cs](../../../../src/compiler/z42.Semantics/Bound/BoundExprVisitor.cs)（如存在）— `null` 元素跳过 visit 或显式处理

## 阶段 4: 测试 + 文档

- [x] 4.1 [NamedArgumentsTests.cs](../../../../src/compiler/z42.Tests/NamedArgumentsTests.cs) NEW — 14 个 case 全绿（Parser 3 + Z1001-Z1005 共 5 + reorder 4 + 2 新增 instance method 覆盖；2026-05-12）
- [ ] 4.2 [src/tests/calls/named_args/](../../../../src/tests/calls/named_args/) NEW — golden e2e（按需引入；现有 stdlib 不依赖此特性）
- [x] 4.3 既有 1223 C# + 320 VM golden 全绿 → ternary `a ? b : c` 未被新 lookahead 误伤
- [ ] 4.4 docs/design/language/language-overview.md — named arguments 语法示例（按需补；feature 在 roadmap 0.6.x 完整登场前可省略）
- [x] 4.5 全绿验证：1225 C# + 309 Rust unit + 320 VM golden（2026-05-12 9 paths wired）
