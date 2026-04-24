# Tasks: 调试符号 — 行号映射

> 状态：🟢 已完成（Phase 1）| 创建：2026-04-18

## Phase 1: 行号映射 ✅
- [x] IrFunction 新增 LineTable（IrLineEntry: block, instr, line, file）
- [x] FunctionEmitter TrackLine: 在 EmitExpr/EmitBoundStmt 入口自动记录行号变化（RLE 压缩）
- [x] Rust VM Function struct 新增 line_table: Vec<LineEntry>
- [x] VM 解释器 exec_function: 错误时解析 line_table 显示源码行号
- [x] JSON IR 格式完整携带 line_table
- [x] 442 compiler + 114 VM tests 全绿

## Phase 2: 二进制格式（follow-up）
- [ ] ZbcWriter/ZbcReader (C#): FUNC section 序列化/反序列化 line_table
- [ ] zbc_reader.rs (Rust): 读取 line_table from binary FUNC section
- [ ] 验证 binary round-trip 保留行号信息
