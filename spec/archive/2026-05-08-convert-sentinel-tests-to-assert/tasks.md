# Tasks: Convert Sentinel Golden Tests to Assert-Only

> 状态：🟢 已完成 | 创建：2026-05-08 | 完成：2026-05-08
> 类型：refactor（最小化模式）

**变更说明：** 把 15 个"满满 Assert + 收尾 Console.WriteLine 哨兵"的 golden 测试转为纯 assert 模式（删 expected_output.txt + 删哨兵 println + 删 using Std.IO）。
**原因：** 哨兵打印不增加测试力度，反而把"断言失败"伪装成"输出 diff"；assert 模式失败信息更精确。
**文档影响：** [src/tests/README.md](../../src/tests/README.md) 加一段说明 "无 expected_output.txt = assert-only 模式"（若 README 尚未提及该约定）。

## 进度概览

- [x] 阶段 1: 单 case 修改（15 个）
- [x] 阶段 2: 文档同步
- [x] 阶段 3: 验证

## 阶段 1: 单 case 修改

每个 case 三步：
1. 删 source.z42 中尾部 `Console.WriteLine("xxx ok");`
2. 删 source.z42 顶部 `using Std.IO;`（确认无其他 Console 引用）
3. 删 expected_output.txt

- [x] 1.1 [src/tests/closures/closure_l3_capture/](../../src/tests/closures/closure_l3_capture/)
- [x] 1.2 [src/tests/closures/closure_l3_loops/](../../src/tests/closures/closure_l3_loops/)
- [x] 1.3 [src/tests/closures/lambda_l2_basic/](../../src/tests/closures/lambda_l2_basic/)
- [x] 1.4 [src/tests/closures/local_fn_l2_basic/](../../src/tests/closures/local_fn_l2_basic/)
- [x] 1.5 [src/tests/control_flow/21_null_conditional/](../../src/tests/control_flow/21_null_conditional/)
- [x] 1.6 [src/tests/control_flow/58_loop_control/](../../src/tests/control_flow/58_loop_control/)
- [x] 1.7 [src/tests/control_flow/65_nested_loops/](../../src/tests/control_flow/65_nested_loops/)
- [x] 1.8 [src/tests/generics/68_generic_function/](../../src/tests/generics/68_generic_function/)
- [x] 1.9 [src/tests/generics/69_generic_class/](../../src/tests/generics/69_generic_class/)
- [x] 1.10 [src/tests/operators/61_logical_operators/](../../src/tests/operators/61_logical_operators/)
- [x] 1.11 [src/tests/operators/62_comparison_operators/](../../src/tests/operators/62_comparison_operators/)
- [x] 1.12 [src/tests/strings/66_string_edge_cases/](../../src/tests/strings/66_string_edge_cases/)
- [x] 1.13 [src/tests/types/52_numeric_aliases/](../../src/tests/types/52_numeric_aliases/)
- [x] 1.14 [src/tests/types/63_nullable_value_types/](../../src/tests/types/63_nullable_value_types/)
- [x] 1.15 [src/tests/types/64_type_conversions/](../../src/tests/types/64_type_conversions/)

## 阶段 2: 文档同步

- [x] 2.1 [src/tests/README.md](../../src/tests/README.md) 检查并补充"assert-only 模式"约定（若未提及）

## 阶段 3: 验证

- [x] 3.1 `./scripts/regen-golden-tests.sh` 重新编译所有 golden（确保 source.z42 改动落地到 source.zbc）
- [x] 3.2 `./scripts/test-vm.sh` 双模式（interp + jit）全绿
- [x] 3.3 `dotnet test src/compiler/z42.slnx` 全绿（确认 refactor 没意外触发 C# 端测试回归）

## Scope（允许改动的文件）

| 文件 | 类型 | 说明 |
|------|------|------|
| `src/tests/closures/closure_l3_capture/source.z42` | MODIFY | 删 Console + Std.IO |
| `src/tests/closures/closure_l3_capture/expected_output.txt` | DELETE | 哨兵 |
| `src/tests/closures/closure_l3_loops/source.z42` | MODIFY | 同上 |
| `src/tests/closures/closure_l3_loops/expected_output.txt` | DELETE | |
| `src/tests/closures/lambda_l2_basic/source.z42` | MODIFY | 同上 |
| `src/tests/closures/lambda_l2_basic/expected_output.txt` | DELETE | |
| `src/tests/closures/local_fn_l2_basic/source.z42` | MODIFY | 同上 |
| `src/tests/closures/local_fn_l2_basic/expected_output.txt` | DELETE | |
| `src/tests/control_flow/21_null_conditional/source.z42` | MODIFY | 同上 |
| `src/tests/control_flow/21_null_conditional/expected_output.txt` | DELETE | |
| `src/tests/control_flow/58_loop_control/source.z42` | MODIFY | 同上 |
| `src/tests/control_flow/58_loop_control/expected_output.txt` | DELETE | |
| `src/tests/control_flow/65_nested_loops/source.z42` | MODIFY | 同上 |
| `src/tests/control_flow/65_nested_loops/expected_output.txt` | DELETE | |
| `src/tests/generics/68_generic_function/source.z42` | MODIFY | 同上 |
| `src/tests/generics/68_generic_function/expected_output.txt` | DELETE | |
| `src/tests/generics/69_generic_class/source.z42` | MODIFY | 同上 |
| `src/tests/generics/69_generic_class/expected_output.txt` | DELETE | |
| `src/tests/operators/61_logical_operators/source.z42` | MODIFY | 同上 |
| `src/tests/operators/61_logical_operators/expected_output.txt` | DELETE | |
| `src/tests/operators/62_comparison_operators/source.z42` | MODIFY | 同上 |
| `src/tests/operators/62_comparison_operators/expected_output.txt` | DELETE | |
| `src/tests/strings/66_string_edge_cases/source.z42` | MODIFY | 同上 |
| `src/tests/strings/66_string_edge_cases/expected_output.txt` | DELETE | |
| `src/tests/types/52_numeric_aliases/source.z42` | MODIFY | 同上 |
| `src/tests/types/52_numeric_aliases/expected_output.txt` | DELETE | |
| `src/tests/types/63_nullable_value_types/source.z42` | MODIFY | 同上 |
| `src/tests/types/63_nullable_value_types/expected_output.txt` | DELETE | |
| `src/tests/types/64_type_conversions/source.z42` | MODIFY | 同上 |
| `src/tests/types/64_type_conversions/expected_output.txt` | DELETE | |
| `src/tests/README.md` | MODIFY | 补充约定（若需要） |

**只读引用**：
- `scripts/test-vm.sh` — 验证 assert-only 分支语义（已支持，无需改）

## 备注

下游（split-imported-symbol-loader 工作树未提交改动）独立，不影响本批次。本批次完成后再开批 2 spec（扁平化目录 + test-vm.sh glob）。
