# Tasks: Split FunctionEmitter.EmitMethod (Function Hard Limit)

> 状态：🟢 已完成 | 创建/完成：2026-05-12
> 类型：refactor（单文件 — 最小化模式）

## 变更说明

`FunctionEmitter.EmitMethod` ~115 行（远超 60 行函数硬限）。拆为 dispatcher 41 行 + 3 个职责清晰的 helpers：

- `EmitMethod`（41 行）— 入口：state 初始化、param 寄存器、调用 helper、最终 ret 兜底、返回构造结果
- `EmitCtorChainAndFieldInits`（52 行）— ctor 入口序列：`:this(...)` chain 优先；否则 `:base(...)` + 字段 init 注入
- `EmitInstanceFieldInits`（14 行）— 字段 init 注入逻辑（被 ctor chain helper 调用）
- `BuildEmittedMethodResult`（34 行）— 把 emitter accumulator state 装配为 `IrFunction` record

零行为变更。

## 验证

- ✅ dotnet build 无 error / warning
- ✅ ./scripts/test-all.sh 6 stage 全绿（1233 C# / 320 VM golden / cross-zpkg / 6 stdlib lib）
- ✅ 所有 FunctionEmitter.cs 内函数 ≤ 52 行（硬限 60 内）

## 备注

文件 280 → 304 LOC（doc comments 增加），仍在 300 软限附近。软限 ≠ 必须拆。

预先存在的 `FunctionEmitterExprs.EmitExpr` 实际只有 5 行（dispatcher），nested visitor class `IrEmitExprVisitor` 各 Visit 方法均 < 60 行 — 无硬限违规，前次评估的 "275 lines" 来自 awk 模式 bug。
