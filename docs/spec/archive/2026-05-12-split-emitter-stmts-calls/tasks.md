# Tasks: Split FunctionEmitterStmts.cs + FunctionEmitterCalls.cs

> 状态：🟢 已完成 | 创建：2026-05-12 | 完成：2026-05-12
> 类型：refactor（最小化模式 — 文件/函数级拆分）

## 实施备注

最终文件规模（所有 Codegen 文件 ≤ 293 LOC，全部满足 300 软限）：
- FunctionEmitterCalls.cs 486 → 238（Defaults 117 + Interpolation 103 + AddressOf 89 抽出）
- FunctionEmitterStmts.cs 487 → 212（Loops 182 + Branches 133 抽出）

函数级硬限处理：
- `EmitBoundCall` 180 → 拆为 dispatcher（13）+ 4 kind helpers（Static/Instance/Virtual/Free，max 64）+ `EmitInstanceVCallFallback`（27）
- `EmitInstanceBoundCall` 64 → 38（VCall fallback 抽出）
- `EmitBoundForeach` 63 → 55（`EmitForeachLength` 抽出）
- `EmitBoundTryCatch` 64 → 25（`EmitTryCatchClauses` 抽出）

**预先存在的硬限违规**（不在本 spec scope，记录待 follow-up）：
- `FunctionEmitter.EmitMethod` ~118 行 — 入口点方法，含 ctor 处理 / instance-field-inits 注入 / TestAttributes 收集等多职责
- `FunctionEmitterExprs.EmitExpr` 主 dispatcher — visitor 已就绪但路径多

零行为变更。test-all.sh 6 stage 全绿（1233 C# / 320 VM golden / cross-zpkg / 6 stdlib lib）。

**变更说明**：消除 Codegen 剩余规模问题：
- `FunctionEmitterCalls.cs` 486 LOC（软限 300 超）+ **`EmitBoundCall` 180 行（60 行函数硬限超）**
- `FunctionEmitterStmts.cs` 487 LOC（软限 300 超）+ **`EmitBoundForeach` 63 行（60 行函数硬限轻微超）**

零行为变更。

**原因**：`.claude/rules/code-organization.md` — 函数 60 行硬限**必须**拆；文件 300 行软限**建议**拆。先解决硬限，附带顺手解决软限。

## 进度概览

- [x] Pass 1: Calls.cs 函数级拆分（EmitBoundCall → 4 helpers）
- [x] Pass 2: Calls.cs 文件级拆分（Interpolation + AddressOf 抽 partial）
- [x] Pass 3: Stmts.cs 文件级拆分（Loops + Branches 抽 partial）
- [x] Pass 4: EmitBoundForeach 微缩（如必要）
- [x] GREEN: ./scripts/test-all.sh 6 stage 全绿
- [x] 归档

## Pass 1: Calls.cs 函数级拆分

- [x] 1.1 [FunctionEmitterCalls.cs](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs) — 把 `EmitBoundCall` 4 个 switch case 抽为 4 个 helper：`EmitStaticBoundCall` / `EmitInstanceBoundCall` / `EmitVirtualBoundCall` / `EmitFreeBoundCall`。dispatcher 留 ~10 行

## Pass 2: Calls.cs 文件级拆分

- [x] 2.1 NEW [FunctionEmitterCalls.Interpolation.cs](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.Interpolation.cs) — `EmitInterpolation` + `EmitBoundPart` + `EmitBoundTextPart` + `EmitBoundExprPart` + `EmitConcat`
- [x] 2.2 NEW [FunctionEmitterCalls.AddressOf.cs](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.AddressOf.cs) — `EmitLoadLocalAddrFor` + `EmitLoadElemAddrFor` + `EmitLoadFieldAddrFor`

## Pass 3: Stmts.cs 文件级拆分

- [x] 3.1 NEW [FunctionEmitterStmts.Loops.cs](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.Loops.cs) — `EmitBoundWhile` + `EmitBoundDoWhile` + `EmitBoundFor` + `EmitBoundForeach` + `ClassIterTarget`
- [x] 3.2 NEW [FunctionEmitterStmts.Branches.cs](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.Branches.cs) — `EmitBoundSwitchStmt` + `EmitBoundTryCatch`

## Pass 4: Foreach 微缩（若仍超 60 行）

- [x] 4.1 抽 `EmitForeachLength(collReg, isClassIter, countIsField, def)` 5–8 行 helper

## 验证 + 归档

- [x] 5.1 [src/compiler/z42.Semantics/README.md](../../../../src/compiler/z42.Semantics/README.md) — 同步新 partial 文件
- [x] 5.2 ./scripts/test-all.sh — 6 stage 全绿
- [x] 5.3 mv → `docs/spec/archive/2026-05-12-split-emitter-stmts-calls/`
