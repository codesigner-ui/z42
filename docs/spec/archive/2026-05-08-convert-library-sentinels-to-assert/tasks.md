# Tasks: Convert Library Sentinel Goldens to Assert-Only

> 状态：🟢 已完成 | 完成：2026-05-08 | 创建：2026-05-08
> 类型：refactor（最小化模式）

**变更说明：** 库内 dir-mode goldens 中 2 个"满 Assert + 收尾 Console 哨兵"模式 case 转 assert-only。保 dir 模式（不扁平 — 库 flat .z42 已被 z42-test-runner 占用）。

**原因：** 完成调研报告提到的"库 sentinel 转 assert"项。同款风险与批 1 一致。

**文档影响：** 无。

## 阶段 1: 改写

- [x] 1.1 `src/libraries/z42.collections/tests/20_dict/source.z42`：删 Console + Std.IO + expected
- [x] 1.2 `src/libraries/z42.collections/tests/40_list_operations/source.z42`：同上

## 阶段 2: 验证

- [x] 2.1 `./scripts/regen-golden-tests.sh --no-stdlib` 全绿
- [x] 2.2 `./scripts/test-vm.sh` interp + jit 全绿
- [x] 2.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿

## Scope

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/libraries/z42.collections/tests/20_dict/source.z42` | MODIFY | 删 Console + Std.IO |
| `src/libraries/z42.collections/tests/20_dict/expected_output.txt` | DELETE | 哨兵 |
| `src/libraries/z42.collections/tests/40_list_operations/source.z42` | MODIFY | 同上 |
| `src/libraries/z42.collections/tests/40_list_operations/expected_output.txt` | DELETE | |
