# Tasks: 消除 StoreInstr/LoadInstr，实现纯寄存器机

> 状态：🟢 已完成 | 创建：2026-04-18 | 完成：2026-04-18

**变更说明：** 将 IR 从混合寄存器/命名槽机改为纯寄存器机，消除 `StoreInstr`/`LoadInstr`，所有变量存取用整数寄存器 ID。

**原因：** 字符串 key 的 HashMap 查找比整数索引慢一个数量级；纯寄存器机是 JIT 实现的前置条件。

**文档影响：** `docs/design/ir.md` 已更新。

---

## 核心任务

### Phase 1: 分析和设计
- [x] 1.1 确认 IR 中所有 StoreInstr/LoadInstr 的使用位置
- [x] 1.2 设计变量→寄存器映射方案

### Phase 2: 编译器侧改造（C#）
- [x] 2.1 修改 IrModule.cs — 删除 StoreInstr/LoadInstr
- [x] 2.2 修改 FunctionEmitter.cs — 变量→寄存器映射
- [x] 2.3 验证编译器侧完整性

### Phase 3: 运行时侧改造（Rust）
- [x] 3.1 修改 frame.rs — 删除 vars HashMap
- [x] 3.2 修改 interp/mod.rs — 删除 Store/Load 执行分支
- [x] 3.3 验证运行时完整性

## 关键 commits
- `e15a301` feat(ir+vm): P0.3 消除 StoreInstr/LoadInstr — 纯寄存器机改造
- `3e5dc93` refactor(jit): review.md 改进 2.2 — 清除命名变量槽残留代码，确认纯寄存器机
