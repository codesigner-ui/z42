# Tasks: disasm 可读性提升

> 状态：🟢 已完成 | 创建：2026-04-19 | 完成：2026-04-19

**变更说明：** 提升 ZasmWriter 的输出可读性：类型标注、行号表

**原因：** M6 要求"disasm 反汇编输出可读性"

**文档影响：** 无

---

- [x] 1.1 寄存器类型标注：`%1:i64 = add` 替代 `%1 = add`（Unknown 类型不显示后缀）
- [x] 1.2 显示行号表（.linetable section）
- [x] 1.3 更新 5 个 IR golden test expected.zasm
- [x] 2.1 验证：dotnet test 456 passed, ./scripts/test-vm.sh 126 passed
