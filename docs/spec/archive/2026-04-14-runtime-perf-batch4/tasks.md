# Tasks: 运行时性能优化（批次 4）

> 状态：🟢 已完成 | 创建：2026-04-14 | 完成：2026-04-14

**变更说明：** H5 find_block O(n)→O(1) + M2 Frame.regs HashMap→Vec
**原因：** 消除解释器热路径中的哈希查找开销
**文档影响：** 无（内部实现改动，不改 IR 语义）

- [x] 1. 创建 openspec
- [x] 2. C# 侧：IrFunction 加 MaxReg 字段，IrGen 填充
- [x] 3. Rust 侧：Function 加 max_reg 字段（serde default）
- [x] 4. Rust 侧：Frame.regs HashMap → Vec<Value>，ops.rs 参数改为 &[Value]
- [x] 5. Rust 侧：exec_function 预建 block_map，替换 find_block/find_handler
- [x] 6. 验证全绿并提交
