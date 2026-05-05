# Tasks: Migrate Runtime Tests by Ownership (dotnet/runtime-style)

> 状态：🟢 已完成 | 创建：2026-05-05 | 完成：2026-05-05 | 类型：refactor

**变更说明**：对标 dotnet/runtime 测试目录组织，把 `src/runtime/tests/golden/` 121 个 case 按归属拆分到 `src/tests/<category>/` 与 `src/libraries/<lib>/tests/`，拍平 `golden/` 中间层。

**原因**：
- 当前 `src/runtime/tests/golden/run/` 121 个 case 扁平堆放，无类别区分，CI 无法做最小化执行
- 标准库 case 与 VM core case 混居，违反"被测对象在哪、测试就在哪"的归属规则
- `golden/` 子目录是历史命名，无功能含义，徒增路径深度
- dotnet/runtime 模式（src/tests/{Exceptions,JIT,GC,Loader,...}）已被工业级项目验证

**文档影响**：[docs/design/testing.md](docs/design/testing.md) 测试目录章节重写

---

## 目标布局

```
src/
├── compiler/z42.Tests/                # 编译器单元测试（C# xUnit，不变）
├── runtime/
│   ├── src/<mod>_tests.rs             # VM Rust 单测（不变）
│   └── tests/                         # 仅保留 Rust integration .rs + data/
│       ├── zbc_compat.rs
│       ├── native_*.rs
│       ├── manifest_schema_validation.rs
│       └── data/
├── libraries/<lib>/tests/             # stdlib 库本地测试（拍平 golden/）
│   ├── <NN>_<name>/                   # 原 tests/golden/<NN>_<name>/
│   └── *.z42                          # [Test] 格式
└── tests/                             # ★ 新中央 VM 测试根
    ├── README.md
    ├── basic/         01_hello, 02_fibonacci, 03_fizzbuzz, 04_arrays, 05_foreach,
    │                  09_ternary, 10_compound_assign, 13_assert, 22_namespace_basic,
    │                  25_zlib_format, 39_inherited_fields, 45_is_pattern_binding,
    │                  46_object_protocol, 48_default_params, 53_expression_body
    ├── exceptions/    12_exceptions, 41_try_finally, 57_nested_exceptions,
    │                  60_finally_propagation, 67_stack_trace, 91_exception_base,
    │                  92_exception_subclass
    ├── generics/      68_generic_function, 69_generic_class, 70_generic_constraints,
    │                  71_generic_baseclass, 72_generic_bare_typeparam,
    │                  73_generic_instantiated_type, 75_generic_primitive_interface,
    │                  84_generic_enum_constraint, 86_extern_impl_user_class,
    │                  87_generic_inumber, 101_generic_interface_dispatch
    ├── inheritance/   27_inheritance, 28_virtual, 32_abstract,
    │                  51_implicit_object_base, 55_multilevel_virtual
    ├── interfaces/    30_interface, 59_multi_interface, 99_interface_property,
    │                  100_comparer_contract, interface_event
    ├── delegates/     delegate_d1a, delegate_d1b_method_group, delegate_d1c_stdlib,
    │                  multicast_action_basic, multicast_exception_aggregate,
    │                  multicast_func_predicate, multicast_subscription_refs,
    │                  multicast_unsubscribe, event_keyword_multicast,
    │                  event_keyword_singlecast, event_singlecast_idisposable,
    │                  nested_delegate_dotted
    ├── closures/      closure_l3_capture, closure_l3_loops, closure_l3_mono,
    │                  closure_l3_stack, lambda_l2_basic, local_fn_l2_basic
    ├── gc/            110_gc_cycle, 111_gc_collect_during_exec, 112_gc_jit_transitive,
    │                  weak_ref_basic, weak_subscription_alive, weak_subscription_lapsed,
    │                  composite_ref_weak_mode
    ├── types/         19_enum, 31_record, 33_typeof, 34_is_as, 47_struct,
    │                  52_numeric_aliases, 54_char_type, 63_nullable_value_types,
    │                  64_type_conversions
    ├── control_flow/  08_switch, 16_do_while, 17_null_coalesce, 21_null_conditional,
    │                  56_switch_statement, 58_loop_control, 65_nested_loops,
    │                  82_short_circuit
    ├── operators/     24_bitwise, 35_prefix_increment, 36_parse_and_string_static,
    │                  37_postfix_expr_context, 61_logical_operators,
    │                  62_comparison_operators, 88_operator_overload,
    │                  89_static_abstract_operator
    ├── refs/          21_ref_local, 21b_out_var, 21c_in_param, 21d_ref_nested
    ├── classes/       07_class_basic, 23_access_control, 26_static_methods,
    │                  29_auto_property, 42_static_fields, 43_static_method_cross_call,
    │                  50_tostring_override, 79_indexer_basic,
    │                  95_class_self_reference_field, 96_ctor_overload,
    │                  97_auto_property_class, class_field_default_init
    ├── strings/       06_string_builtins, 14_string_methods, 44_string_static_methods,
    │                  66_string_edge_cases, 90_string_script
    ├── errors/        ← 整体搬自 src/runtime/tests/golden/errors/
    └── cross-zpkg/    ← 整体搬自 src/runtime/tests/cross-zpkg/

stdlib-bound (移到 libraries):
    z42.collections: 18_list, 20_dict, 20_dict_iter, 40_list_operations,
                     76_generic_list, 83_foreach_user_class
    z42.io:          16_path
```

---

## 阶段 1: 建 src/tests/ 目录骨架

- [x] 1.1 mkdir 14 个类别子目录 + errors/ + cross-zpkg/
- [x] 1.2 写 src/tests/README.md（类别说明 + 归属规则）

## 阶段 2: 移动 121 个 case

- [x] 2.1 移 7 个 stdlib-bound case 到 `src/libraries/<lib>/tests/<case>/`（保留 golden 子目录中已有的，flatten 到 tests/ 根）
- [x] 2.2 拍平既存 `src/libraries/<lib>/tests/golden/` → `src/libraries/<lib>/tests/`
- [x] 2.3 移 112 个 vm_core case 到 `src/tests/<category>/<case>/`
- [x] 2.4 移 `src/runtime/tests/golden/errors/` → `src/tests/errors/`
- [x] 2.5 移 `src/runtime/tests/cross-zpkg/` → `src/tests/cross-zpkg/`
- [x] 2.6 删除空 `expected_output.txt`（16 个）
- [x] 2.7 删除空 `src/runtime/tests/golden/` 目录

## 阶段 3: 更新脚本与代码

- [x] 3.1 [scripts/test-vm.sh](scripts/test-vm.sh) `GOLDEN_GLOBS` 改新路径
- [x] 3.2 [scripts/regen-golden-tests.sh](scripts/regen-golden-tests.sh) `GOLDEN_GLOBS` 改新路径
- [x] 3.3 [scripts/test-cross-zpkg.sh](scripts/test-cross-zpkg.sh) `TESTS_DIR` 改 `src/tests/cross-zpkg`
- [x] 3.4 [src/runtime/tests/zbc_compat.rs](src/runtime/tests/zbc_compat.rs) `golden_dir()` 改新根 + glob
- [x] 3.5 [src/compiler/z42.Tests/GoldenTests.cs](src/compiler/z42.Tests/GoldenTests.cs) `GoldenRoot` + 发现逻辑改造

## 阶段 4: 文档同步

- [x] 4.1 重写 [docs/design/testing.md](docs/design/testing.md) 测试目录章节（dotnet/runtime 风格）
- [x] 4.2 更新 [.claude/rules/code-organization.md](.claude/rules/code-organization.md) 测试目录约定
- [x] 4.3 更新 [src/runtime/tests/](src/runtime/tests/) 之外的所有 README 引用（cross-zpkg/README.md 已就地搬）

## 阶段 5: 验证

- [x] 5.1 `dotnet build src/compiler/z42.slnx` 无错
- [x] 5.2 `cargo build --manifest-path src/runtime/Cargo.toml` 无错
- [x] 5.3 `./scripts/regen-golden-tests.sh` 全过（重新生成 .zbc）
- [x] 5.4 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全过
- [x] 5.5 `./scripts/test-vm.sh` 全过（interp + jit）
- [ ] 5.6 `./scripts/test-cross-zpkg.sh` — ⚠️ pre-existing 失败（与本变更无关），已登记到 [docs/deferred.md](docs/deferred.md) `fix-cross-zpkg-using-resolution`

## 阶段 6: 提交

- [x] 6.1 commit + push（含 `.claude/`、`spec/`、所有移动的文件）
- [x] 6.2 归档 spec 到 `spec/archive/2026-05-05-migrate-runtime-tests-by-ownership/`

---

## 备注

- **保留 `expected_output.txt`（非空）**：103 个非空文件是 stdout 比对必需，等 R3 z42-test-runner 落地后由独立 spec 替换
- **删除空 `expected_output.txt`（16 个）**：用例已用 `Assert.Equal` 自验，空文件无信息
- **`src/runtime/tests/` 保留**：cargo 框架强约定 Rust 集成测试位置；保留 `zbc_compat.rs` / `native_*.rs` / `manifest_schema_validation.rs` / `data/`
- **git mv 保留历史**：所有移动用 `git mv`，不用 `mv` + `git add`
- **类别粒度**：14 个 + parse + errors + cross-zpkg = 17 个顶层；后续可演化（合并 / 拆分）

## 实际产出

- **121 个 run case** 移到目标位置（114 vm_core + 7 stdlib-bound）
- **5 个 parse-only case** 单独归到 `src/tests/parse/`（原 `golden/01_hello/02_control_flow/03_expressions/`，被首次发现的遗漏）
- **18 个空 expected_output.txt** 删除（用 `Assert.*` 自验的用例）
- **2 个补 expected_output.txt**：`21_null_conditional` / `20_dict` —— 原 test-vm.sh 的 `[ -f "$expected" ] || continue` 逻辑把这两个用例**静默跳过**了，迁移后修正为正确执行 + stdout 比对（迁移意外揭露 dead test）
- 既存 `src/libraries/<lib>/tests/golden/<case>/` 拍平到 `src/libraries/<lib>/tests/<case>/`（13 例：collections 8 + math 3 + text 1 + test 1）
- **xUnit GoldenTests 改造**：新 `FindProjectRoot` 双路径回退（AppContext.BaseDirectory + Environment.CurrentDirectory），分类逻辑改用文件存在判定 + dir lineage（不再依赖 `/run/` 路径片段）
- **测试结果**：xUnit 1053/1053 + Rust 261/261 + interp golden 134/134 + jit golden 130/130 全绿；cross-zpkg 1 例 pre-existing 失败已 deferred
