# Tasks: Convert Remaining classes/ Goldens to Assert

> 状态：🟢 已完成 | 完成：2026-05-09 | 创建：2026-05-09
> 类型：refactor（最小化模式）

**变更说明：** 8 个 `src/tests/classes/` 下仍有 expected_output.txt 的 case 转 assert + 扁平化。即便含 `$"..."` 插值，测的是字段值/dispatch/ToString 行为，可用 `Assert.Equal(<literal>, expr)` 等价表达（或保留插值在 RHS 测同款语义）。

**清单：**
- arity_method_dispatch / arity_overloading
- auto_property / auto_property_class
- class_basic / class_field_default_init
- class_self_reference_field
- tostring_override

## 阶段 1: 转换 + 扁平化（8 个）
- [x] 1.1 - 1.8 同模式

## 阶段 2: 验证
- [x] 2.1 regen-golden + test-vm interp/jit + dotnet test 全绿

## Scope
8 case × (源 MODIFY + expected DELETE + dir → flat RENAME)
