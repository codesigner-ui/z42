# Tasks: Golden Test 补全

> 状态：🟢 已完成 | 创建：2026-04-19 | 完成：2026-04-19

**变更说明：** 补全 L1 特性的 golden test 覆盖，确保每个特性至少有正常用例、边界用例

**原因：** M6 要求"Golden test 覆盖所有 L1 特性（每特性至少：正常用例、边界用例、错误用例）"

**文档影响：** 无

---

## Run tests 补全

- [x] 1.1 `61_logical_operators` — &&, ||, !, 短路求值验证
- [x] 1.2 `62_comparison_operators` — ==, !=, <, >, <=, >= 全类型覆盖
- [x] 1.3 `63_nullable_value_types` — int? 声明、null 赋值、?? 组合使用
- [x] 1.4 `64_type_conversions` — 隐式拓宽转换（int→long）、算术提升
- [x] 1.5 `65_nested_loops` — 嵌套循环 + break/continue 语义边界
- [x] 1.6 `66_string_edge_cases` — 空字符串、嵌套插值、Contains

## Error tests 补全

- [x] 2.1 `22_wrong_arg_count` — 函数调用参数数量不匹配 (E0402)
- [x] 2.2 `23_assign_type_mismatch` — 变量赋值类型不匹配 (E0402)
- [x] 2.3 `24_missing_return` — 非 void 函数缺少 return (E0403)
- [x] 2.4 `25_void_in_expression` — void 赋值给变量 (E0409)

## 验证

- [x] 3.1 `dotnet build` 无编译错误
- [x] 3.2 `dotnet test` 全绿 — 456 passed
- [x] 3.3 `./scripts/test-vm.sh` 全绿 — 126 passed (interp 63 + jit 63)
