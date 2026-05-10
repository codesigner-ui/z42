# Tasks: 预计算函数块索引

> 状态：🟢 已完成 | 创建：2026-04-18 | 完成：2026-04-18

**变更说明：** 模块加载时预计算每个函数的块标签→索引映射，消除每次函数调用的 O(n) HashMap 重建

**原因：** 解释器调用性能瓶颈；热点函数的循环调用累积这个开销

**文档影响：** 无（纯内部性能优化）

## 实施清单

- [x] 1.1 在 Function struct 中添加 block_index 字段
- [x] 1.2 修改 Module 加载流程初始化时计算 block_index
- [x] 1.3 更新 interp/mod.rs::exec_function 使用预计算的 block_index
- [x] 1.4 验证：dotnet test && ./scripts/test-vm.sh 全绿
- [x] 1.5 归档

## 关键 commits
- `e09cf5c` perf(vm): P1.2 预计算函数块索引 — 消除解释器调用 O(n) 开销
