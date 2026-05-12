# Tasks: 修复 native interop integration tests 残留问题（param_types 缺失 + Z-code 遗留断言）

> 状态：🟢 已完成 | 创建：2026-05-12 | 归档：2026-05-12 | 类型：fix（test 维护，无生产代码改动）

**变更说明：** 修复 `src/runtime/tests/native_*.rs` 三个集成测试文件中的 6 处编译错误（`Function` 结构体新增 `param_types` 字段后 test fixture 未同步）和 5 处 retire-z-codes 后遗留的 `Z0905` / `Z0908` 字符串断言。

**原因：**

1. **param_types 编译错误**：HEAD ad98e45c (redesign-artifact-layout) 进一步演进了 `Function` 结构体，但下游 6 处 test 构造点未同步；导致 `cargo test` 全部测试 binary 编不过。  
2. **Z-code 遗留断言**：`bab6b357 retire-z-codes` 把 `Z0905`/`Z0908` 等运行时错误码退役为 typed exception (`Std.InvalidMarshalException`)，但这三个 test 文件由于"编不过"被 GREEN gate 跳过，断言一直停留在旧 Z-code 字符串上。修编译错误后这 5 个断言全部暴露 RED。

**文档影响：** 无（纯 test 维护，不改动外部行为或机制）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/tests/native_opcode_trap.rs`           | MODIFY | 1 处加 `param_types: vec![]`；3 个 test 断言改 `Z0905`/`Z0908` → 新错误关键字；test 名去 `_z09xx` 后缀 |
| `src/runtime/tests/native_pin_e2e.rs`               | MODIFY | 1 处加 `param_types: vec![]`；3 个 test 断言同上 |
| `src/runtime/tests/native_interop_e2e.rs`           | MODIFY | 4 处加 `param_types: vec![]`；2 个 test 断言同上 |
| `docs/spec/changes/fix-native-test-residual/tasks.md` | NEW | 本文件 |

## 任务清单

- [x] 1.1 加 `param_types: vec![],` 至 6 处 `Function { ... }` literal 构造
- [x] 1.2 更新 `callnative_unknown_method_z0905` → `callnative_unknown_method_traps`，断言改为 "unknown method" / "ghost_method"
- [x] 1.3 更新 `z42_str_with_interior_nul_traps_z0908` → `..._marshal`，断言改为 "InvalidMarshalException"
- [x] 1.4 更新 `call_native_unknown_type_z0905` → `..._traps`，断言改为 "unknown native type"
- [x] 1.5 更新 `pin_ptr_non_str_z0908` → `..._traps`，断言改为 "InvalidMarshalException"
- [x] 1.6 更新 `unpin_ptr_non_view_z0908` → `..._traps`，断言改为 "UnpinPtr expects PinnedView"
- [x] 1.7 更新 `pin_view_unknown_field_z0908` → `..._traps`，去掉 Z0908 断言（保留 "PinnedView" / "lulz"）
- [x] 1.8 更新 `pin_array_with_out_of_range_element_z0908` → `..._traps`，断言改为 "InvalidMarshalException"
- [x] 1.9 更新 `pin_array_with_negative_element_z0908` → `..._traps`，断言改为 "InvalidMarshalException"
- [x] 1.10 GREEN 验证：`cargo test --manifest-path src/runtime/Cargo.toml`（339 tests pass）+ `dotnet test`（1233/1233）+ `./scripts/test-vm.sh`（320/320）
- [x] 1.11 commit + push（fix type）

## 备注

- 留 stub：`pin_ptr_non_str_traps` 和 `pin_array_with_*_traps` 在 stdlib-less 测试模块下走 "stdlib type `Std.InvalidMarshalException` not loaded; cannot construct exception" 路径，断言仅检查 "InvalidMarshalException" 字串足以确认 marshal 失败路径被命中。当本仓库引入 e2e 框架（含 stdlib bootstrapping）时可改为断言真实异常类型。
- 不动 `pin_str_then_field_ptr_returns_nonzero` 等正路测试（已通过）。
