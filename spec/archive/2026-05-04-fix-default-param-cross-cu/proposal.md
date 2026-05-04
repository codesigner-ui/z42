# Proposal: D-9 — fix default param fill for cross-CU calls

## Why

调用 stdlib（imported CU）方法 `bus.Invoke(arg)` 当方法签名为 `void Invoke(T, bool=false)` 时，z42 编译器漏填默认参数 → VM 报 `expected bool in register %2, got Null`。D2d-2 已用 1-arg/2-arg overload workaround，本 spec 修根因。

**根因定位**（[FunctionEmitterCalls.cs:184-198](src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs#L184-L198) `FillDefaults`）：
- `_funcParams` 仅 [IrGen.cs:138-169](src/compiler/z42.Semantics/Codegen/IrGen.cs#L138-L169) 里 LOCAL CU `cu.Classes` / `cu.Functions` 注册
- 跨 CU 方法（imported）`_funcParams` 查 miss → 直接 `return argRegs;` 不补默认 → VM dispatch 时空寄存器残留 Null

**修复路径**：TSIG 已导出 `MinArgCount` ([ExportedTypes.cs:103,116](src/compiler/z42.IR/ExportedTypes.cs#L103))，`SemanticModel.Classes` 经 `MergeImported` 后含 imported `Z42ClassType` with `Z42FuncType.RequiredCount` 完整。FillDefaults 缺一个 fallback 路径用 `Z42FuncType.Params[i]` 类型 emit type-default const。

## What Changes

- 加 `IEmitterContext.TryGetMethodSignature(qualifiedName, out Z42FuncType)` API
- IrGen 初始化时遍历 `SemanticModel.Classes`（含 imported）+ `SemanticModel.Funcs`，注册 qualifiedName → Z42FuncType（与 `_funcParams` 同款 key 格式）
- `FillDefaults` fallback：当 `_funcParams` miss 时查 `_funcSignatures`，对 [argRegs.Count, Params.Count) 的缺位 emit type-default const
  - bool → ConstBool(false)
  - int / long / short / byte / sbyte / etc. → ConstI32/I64(0)
  - double / float → ConstF64/F32(0.0)
  - string / Object / Class / Interface / Option / Array / FuncType → ConstNull
- 移除 D2d-2 + D-9 workaround：MulticastAction / MulticastFunc / MulticastPredicate 1-arg overload 退回单签名 default param 形式

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/Codegen/IEmitterContext.cs` | MODIFY | 加 `TryGetMethodSignature` |
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | MODIFY | 初始化 `_funcSignatures`；实现 IEmitterContext 新接口 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs` | MODIFY | `FillDefaults` 加 imported 路径 + `EmitTypeDefault(Z42Type)` helper |
| `src/libraries/z42.core/src/MulticastAction.z42` | MODIFY | 还原 1-arg overload，回到 default param 形式 |
| `src/libraries/z42.core/src/MulticastFunc.z42` | MODIFY | 同上 |
| `src/libraries/z42.core/src/MulticastPredicate.z42` | MODIFY | 同上 |
| `src/compiler/z42.Tests/CrossCuDefaultParamTests.cs` | NEW | 单元测试 |
| `src/runtime/tests/golden/run/cross_cu_default_param/source.z42` | NEW | 端到端 golden |
| `src/runtime/tests/golden/run/cross_cu_default_param/expected_output.txt` | NEW | 预期输出 |

## Out of Scope

- 用户自定义 default value 跨 CU 传递（如 `int x = 42`）—— 本 spec 用 type-default 兜底（`int x = 42` 调用方将拿到 0 而非 42）。完整 default value 传递需要 TSIG 扩展 `ExportedParamDef.DefaultValue`，留独立 follow-up
- 文档：明示用户对跨 CU 调用 default value 限制

## Open Questions

- [ ] 跨 CU default 与 local CU 的 BoundDefault 行为不一致 —— 用户写 `int x = 42` 在同 CU 调用拿 42，跨 CU 拿 0。是否报警告 / 文档警示？倾向：仅文档注释（用户用 type-default 即可，不为非 type-default 默认值报警增加 friction）
