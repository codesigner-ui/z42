# Tasks: L3-G4h Step 1 — `&&` / `||` 短路求值

> 状态：🟢 已完成 | 完成：2026-04-22

**变更说明：** IR 层 `&&` / `||` desugar 为 BrCond 控制流，右侧只在需要时求值。
**原因：** G4g HashMap.FindSlot 发现 `occupied[s] && keys[s].Equals(k)` 在 occupied=false 时仍 VCall Null；目前嵌套 if 规避。替换 pseudo-class List/Dict 需先消除该陷阱。
**文档影响：** `docs/design/language-overview.md`（短路语义）、`docs/design/generics.md`（G4g 限制列表移除短路项）、`docs/roadmap.md`（G4h 进度）。

## 任务

- [x] 1.1 `FunctionEmitterExprs.cs`: `EmitBoundBinary` 对 `BinaryOp.And/Or` 走 BrCond 短路路径，不再走 `BinFactory`
- [x] 1.2 Golden test `82_short_circuit/`: 覆盖 `&&` 左假不求右、`||` 左真不求右、嵌套混合
- [x] 1.3 HashMap.z42 `FindSlot` 回归为自然 `&&` 写法（验证真的短路）
- [x] 1.4 `docs/design/language-overview.md` 增补短路语义描述（§3.5）
- [x] 1.5 `docs/design/generics.md` G4g 限制列表删除 "&&/|| 不短路" 项
- [x] 1.6 `docs/roadmap.md` G4h 行更新 step1 进度
- [x] 1.7 GREEN: dotnet build / cargo build / dotnet test / test-vm.sh 全绿

## 备注

- IR 层 `AndInstr`/`OrInstr` 保留（仅服务 bitwise `&` / `|`）
- 仅改 codegen；Parser/TypeChecker 不动
- 旧 IrGen 测试 `LogicalAnd_EmitsAndInstr` / `LogicalOr_EmitsOrInstr` 更新为 `*_EmitsShortCircuitBranch`，验证 BrCond 且不再出现 AndInstr/OrInstr
