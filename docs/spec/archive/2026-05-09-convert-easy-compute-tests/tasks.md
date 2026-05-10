# Tasks: Convert 28 Easy Compute Goldens to Assert

> 状态：🟢 已完成 | 完成：2026-05-09 | 创建：2026-05-09
> 类型：refactor（最小化模式）

**变更说明：** 28 个直接打印计算值/方法返回值的 case 转 assert + 扁平化。
中等难度（Logger/handler 内部 print）和保 golden 的 case 不在本批。

清单见各 category：basic(3) / control_flow(1) / exceptions(3) / gc(6) /
inheritance(4) / interfaces(2) / operators(4) / types(4) / delegates(1) = 28。

## 阶段 1: 转换（28 个）
- [x] 1.1 同模式: git mv source.z42 → flat.z42, 改 Console→Assert, 删 expected_output.txt

## 阶段 2: 验证
- [x] 2.1 regen-golden + test-vm interp/jit + dotnet test 全绿
