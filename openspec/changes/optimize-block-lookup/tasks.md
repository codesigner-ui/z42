# Tasks: 预计算函数块索引

**变更说明：** 模块加载时预计算每个函数的块标签→索引映射，消除每次函数调用的 O(n) HashMap 重建

**原因：** 解释器调用性能瓶颈；热点函数的循环调用累积这个开销

**文档影响：** 无（纯内部性能优化）

## 实施清单

- [ ] 1.1 在 `Function` struct 中添加 `block_index: HashMap<String, usize>` 字段
- [ ] 1.2 修改 `Module` 加载流程或 `Function::new` 初始化时计算 `block_index`
- [ ] 1.3 更新 `interp/mod.rs::exec_function` 使用预计算的 `block_index` 替代局部 `block_map`
- [ ] 1.4 验证：`dotnet test && ./scripts/test-vm.sh`（所有测试绿灯）
- [ ] 1.5 归档

## 备注

[实施中记录]
