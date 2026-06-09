# Tasks: fix-runtime-unit-test-rot

> 状态：🟢 已完成并归档（2026-06-09）

**变更说明：** 修 5 个 rotted rust 单测，它们因 CI 覆盖盲区长期未被发现：Windows build-and-test 的 `cargo test` 是唯一编 rust 单测的腿，但先被 E0063（ClassDesc.attributes，f2c93971 已修）挡住编译 → 这些单测从未真正跑过。E0063 修后它们终于运行并暴露：

- `metadata::zbc_reader::tests::zbc_version_constants_pinned` / `zpkg_version_constants_pinned`：pin 旧值（9 / 11），C3a/C3b bump 到 1.11 / 0.13 时漏更新这两个 pinning 测试。
- `corelib::tests::obj_get_type_{returns_type_object,simple_name_no_namespace,namespaced_class_splits_name}`：C2（make-typeof-return-type）让 `build_type` 依赖 `Std.Type` 已载入（裸 ctx → `build_type` 返回 Null）；这 3 个用裸 `ctx()` 的测试没跟着更新（同期 `reflection_tests.rs` 已用 C2/C3 模式）。

**原因 + 修复：**
- version pins：更新断言到当前常量 1.11 / 0.13（pinning 测试本就该随 bump 更新，per version-bumping.md）。
- obj_get_type：加 `ctx_with_std_type()` 测试 helper（`install_lazy_loader` + `seed_lazy_loader_types` 注入含 `__name`@0/`__fullName`@1 的最小 `Std.Type` TypeDesc），3 个测试改用之，真正走 `Std.Type` 生产路径。

**文档影响：** 无（test-only；不改 wire format / 行为）。

**子系统：** `runtime`（空闲）。fix 型，minimal mode。

- [x] 1.1 `metadata/zbc_reader_tests.rs`：zbc 9→11、zpkg 11→13 + 注释
- [x] 1.2 `corelib/tests.rs`：`ctx_with_std_type()` helper + 3 obj_get_type 测试改用
- [x] 1.3 验证：targeted `cargo test obj_get_type version_constants_pinned` → 7/7 ok（5 修复 + 2 相邻）

## 备注
- 根因是 **CI 覆盖盲区**：rust 单测仅 Windows 腿编/跑，且长期被 E0063 挡编译 → 集体腐烂。本批 + 已修的 E0063（f2c93971）+ parser crash（34152762）一起让 Windows `cargo test` 终于可绿。
- 仍可能有**仅 Windows**（路径/CRLF）的 rust 测试失败 macOS 复现不到；需 Windows 腿实跑确认。
