# Tasks: feat-stringbuilder
**变更说明：** 实现 `Std.Text.StringBuilder`，替换占位符
**原因：** 字符串拼接场景需要 O(1) amortized append；占位符无法编译使用
**文档影响：** 无（stdlib 内部实现，不改语言规范）

- [ ] 1.1 创建 `src/runtime/src/corelib/string_builder.rs` — `builtin_sb_append`
- [ ] 1.2 注册 `pub mod string_builder` + `__sb_append` 到 `corelib/mod.rs`
- [ ] 1.3 更新 `src/libraries/z42.text/src/StringBuilder.z42` — 完整实现
- [ ] 1.4 重新构建标准库：`./scripts/build-stdlib.sh`
- [ ] 1.5 添加 golden test `49_stringbuilder`
- [ ] 1.6 验证：`dotnet build && cargo build && dotnet test && ./scripts/test-vm.sh`
