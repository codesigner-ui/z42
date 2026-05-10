# Tasks: Convert Multi-line Compute Goldens to Assert

> 状态：🟢 已完成 | 完成：2026-05-09 | 创建：2026-05-09
> 类型：refactor（最小化模式）

**变更说明：** 批 3 filter（`expected ≤ 3 行`）漏掉了循环 / 多次 Console 输出计算值的场景（如 `fibonacci` 循环打印 10 个 Fib(i)）。补一批 ~20 个明显的 compute 模式 case。

**原因：** 这些 case 用 `Console.WriteLine(<computed_value>)` 输出真实计算结果，golden 比对仅是间接断言。直接 Assert 更精确（失败信息含具体期望/实际值），无格式信息丢失。

**保留 golden 的边界 case 不在本批：** auto_property_class、class_self_reference_field、exceptions/exceptions、exception_base/subclass、multicast_exception_generic、weak_ref_basic、switch_statement、string_script、string_static_methods、array_get_type、gc_handle、gc_stats（这 12 个测 toString 格式 / 异常格式 / 顺序）。

## 阶段 1: 转换（~20 个）

每个：源内 `Console.WriteLine(expr)` → `Assert.Equal(<expected>, expr)`；循环情形用 expected 数组迭代；删 `using Std.IO`；删 `expected_output.txt`；扁平化（除非有 sidecar）。

- [x] 1.1 src/tests/basic/fibonacci/
- [x] 1.2 src/tests/basic/fizzbuzz/
- [x] 1.3 src/tests/classes/indexer_basic/
- [x] 1.4 src/tests/classes/ctor_overload/
- [x] 1.5 src/tests/classes/static_methods/
- [x] 1.6 src/tests/closures/closure_l3_stack/
- [x] 1.7 src/tests/control_flow/short_circuit/
- [x] 1.8 src/tests/delegates/delegate_d1b_method_group/
- [x] 1.9 src/tests/delegates/multicast_func_predicate/
- [x] 1.10 src/tests/generics/generic_instantiated_type/
- [x] 1.11 src/tests/generics/generic_interface_dispatch/
- [x] 1.12 src/tests/generics/generic_inumber/
- [x] 1.13 src/tests/generics/generic_primitive_interface/
- [x] 1.14 src/tests/interfaces/comparer_contract/
- [x] 1.15 src/tests/interfaces/interface_property/
- [x] 1.16 src/tests/operators/bitwise/
- [x] 1.17 src/tests/operators/operator_overload/
- [x] 1.18 src/tests/operators/static_abstract_operator/
- [x] 1.19 src/tests/types/array_clone/
- [x] 1.20 src/tests/types/struct/

## 阶段 2: 验证

- [x] 2.1 `./scripts/regen-golden-tests.sh --no-stdlib` 全绿
- [x] 2.2 `./scripts/test-vm.sh` interp + jit 全绿
- [x] 2.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿

## 备注

逐个看是否真没有打印格式语义；若发现"伪 compute 实际测 ToString"立即停下汇报。
