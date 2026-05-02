# Tasks: 闭包单态化

> 状态：🟢 已完成 | 创建：2026-05-02 | 完成：2026-05-02 | 类型：lang/ir（完整流程）
>
> **实施备注**：
> 1. 范围实际收敛为"alias 子集"：`var f = Helper; f();` 类模式 → 直接 Call。
> 2. spec.md 中"no-capture lambda 立即调用单态化"场景未实现（lifted lambda name
>    在 Codegen 时生成，TypeChecker 无法预测；视为 follow-up）。
> 3. 前置 bug 修复：Codegen `BoundIdent` 分支补齐"顶层函数 / 静态方法 → LoadFn"
>    路径，否则 `var g = f;` / `Apply(Helper, …)` 等场景在 Codegen 崩溃。
> 4. 流敏感（if/else 二选一）/ 跨 capture 边界 alias / 完整档 B 单态化作 follow-up。

## 进度概览
- [x] 阶段 1: TypeChecker 扩展 BoundIdent.ResolvedFuncName + alias 跟踪
- [x] 阶段 2: Codegen callee resolution 优先级
- [x] 阶段 3: 测试（单元 + golden）
- [x] 阶段 4: 验证 + 文档同步 + 归档

## 阶段 1: TypeChecker
- [x] 1.1 `BoundExpr.cs` — `BoundIdent` 增加 `string? ResolvedFuncName = null`
- [x] 1.2 `TypeEnv.cs` — 新增 `_aliasScopeStack: Stack<Dictionary<string,string>>` + EnterScope / ExitScope / BindAlias / RemoveAlias / LookupAlias
- [x] 1.3 `TypeChecker.Exprs.cs::BindIdent` — 查 alias / top-level / static method，填 ResolvedFuncName
- [x] 1.4 `TypeChecker.Stmts.cs::BindVarDecl` — 检测 init 是 BoundFuncRef / no-capture BoundLambda / aliased BoundIdent → BindAlias
- [x] 1.5 `TypeChecker.Stmts.cs::BindAssign` — 任何 local var 赋值清 alias（保守）
- [x] 1.6 在 BlockStmt / LambdaBody / LocalFunction 进出点 EnterScope / ExitScope
- [x] 1.7 BindCall 不需要改 — Codegen 侧消费 ResolvedFuncName

## 阶段 2: Codegen
- [x] 2.1 `FunctionEmitterCalls.cs::EmitBoundCall` — 在原有 callee 分支前加：BoundIdent.ResolvedFuncName != null → emit `CallInstr(dst, fqName, args)`
- [x] 2.2 `FunctionEmitterCalls.cs` 或 `FunctionEmitterExprs.cs` — `BoundLambda { Captures.Count: 0 }` 立即调用 → emit `CallInstr(dst, LiftedName, args)`，避免 LoadFn + CallIndirect
- [x] 2.3 检查 `EmitLambdaLiteral` 在 lift 时的 LiftedName 字段是否已暴露在 BoundLambda；若没有补一下
- [x] 2.4 验证现有 `var f = Helper; f();` 路径在改动后正确：不要把别名 var 自身的 `LoadFn` 还 emit（dead code）

## 阶段 3: 测试
- [x] 3.1 NEW `src/compiler/z42.Tests/ClosureMonoCodegenTests.cs` —— 5 个测试（design Testing Strategy）
- [x] 3.2 NEW `src/runtime/tests/golden/run/closure_l3_mono/source.z42`
- [x] 3.3 NEW `src/runtime/tests/golden/run/closure_l3_mono/expected_output.txt`
- [x] 3.4 `./scripts/regen-golden-tests.sh` 生成 zbc

## 阶段 4: 验证 + 文档 + 归档
- [x] 4.1 `dotnet build src/compiler/z42.slnx` 无错
- [x] 4.2 `cargo build --manifest-path src/runtime/Cargo.toml` 无错
- [x] 4.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿（基线 +5）
- [x] 4.4 `./scripts/test-vm.sh` 全绿（基线 +1×2 modes）
- [x] 4.5 spec scenarios 逐条对应实现位置确认
- [x] 4.6 文档同步：
    - `docs/design/closure.md` 新增 §"单态化（impl-closure-l3-monomorphize）"
    - `docs/design/compiler-architecture.md` 在"BoundExpr 设计权衡"里加一句 ResolvedFuncName 来源
    - `docs/roadmap.md` L3-C2 进度表更新（mono ✅）
- [x] 4.7 移动 `spec/changes/impl-closure-l3-monomorphize/` → `spec/archive/2026-05-02-impl-closure-l3-monomorphize/`
- [x] 4.8 commit + push（自动）

## 备注
- 实施时若发现 BoundLambda 没有 LiftedName 字段，在 BoundExpr.cs 同步补
- 若 alias 跟踪误判出现，第一选择是收紧条件（更保守），不要松条件
