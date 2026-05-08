# Tasks: Flatten Assert-Only Test Cases to Single Files

> 状态：🟢 已完成 | 完成：2026-05-08 | 创建：2026-05-08
> 类型：refactor（最小化模式）

**变更说明：** 把 29 个"只剩 source.z42 + 未跟踪 artifacts"的 assert-only golden case 从 `src/tests/<cat>/<NN_name>/source.z42` 扁平化为 `src/tests/<cat>/<NN_name>.z42`。test-vm.sh / regen-golden-tests.sh 升级为双模式（dir + flat 共存）。

**原因：** assert-only case 退化为"1 文件 1 目录"无价值，扁平化后阅读更直接、`git mv` 可见性更好。批 1（convert-sentinel-tests-to-assert）刚把 15 个 case 推到这种状态，结合现存 15 个，合计 30 候选。

**保留目录的 case：**
- `control_flow/08_switch/`：含 `features.toml` sidecar（LanguageFeatures override）→ 保 dir 模式
- 所有有 `expected_output.txt` 的 case → 保 dir 模式（不在本批范围）

**顺手清理：**
- `src/tests/operators/36_parse_and_string_static/expected.txt`（空文件，无任何脚本引用，残留）
- `src/tests/operators/37_postfix_expr_context/expected.txt`（同上）

**文档影响：** `src/tests/README.md` 加 flat 模式说明。

## 进度概览

- [x] 阶段 1: 清理残留 expected.txt
- [x] 阶段 2: git mv 29 个 case 到扁平形态
- [x] 阶段 3: test-vm.sh / regen-golden-tests.sh 双模式适配
- [x] 阶段 4: README 更新
- [x] 阶段 5: 验证

## 阶段 1: 清理残留

- [x] 1.1 删 `src/tests/operators/36_parse_and_string_static/expected.txt`（空，无 consumer）
- [x] 1.2 删 `src/tests/operators/37_postfix_expr_context/expected.txt`（同上）

## 阶段 2: git mv 29 个 case

每个 case：`git mv <dir>/source.z42 <dir>.z42` + `rmdir <dir>`（确保 dir 为空——untracked artifacts 已被 .gitignore，但可能物理存在 → 先确认 dir 内除 source.z42 外仅 untracked）。

- [x] 2.1 src/tests/basic/04_arrays/
- [x] 2.2 src/tests/basic/05_foreach/
- [x] 2.3 src/tests/basic/09_ternary/
- [x] 2.4 src/tests/basic/10_compound_assign/
- [x] 2.5 src/tests/basic/13_assert/
- [x] 2.6 src/tests/basic/39_inherited_fields/
- [x] 2.7 src/tests/closures/closure_l3_capture/
- [x] 2.8 src/tests/closures/closure_l3_loops/
- [x] 2.9 src/tests/closures/lambda_l2_basic/
- [x] 2.10 src/tests/closures/local_fn_l2_basic/
- [x] 2.11 src/tests/control_flow/16_do_while/
- [x] 2.12 src/tests/control_flow/17_null_coalesce/
- [x] 2.13 src/tests/control_flow/21_null_conditional/
- [x] 2.14 src/tests/control_flow/58_loop_control/
- [x] 2.15 src/tests/control_flow/65_nested_loops/
- [x] 2.16 src/tests/generics/68_generic_function/
- [x] 2.17 src/tests/generics/69_generic_class/
- [x] 2.18 src/tests/operators/35_prefix_increment/
- [x] 2.19 src/tests/operators/36_parse_and_string_static/
- [x] 2.20 src/tests/operators/37_postfix_expr_context/
- [x] 2.21 src/tests/operators/61_logical_operators/
- [x] 2.22 src/tests/operators/62_comparison_operators/
- [x] 2.23 src/tests/strings/06_string_builtins/
- [x] 2.24 src/tests/strings/14_string_methods/
- [x] 2.25 src/tests/strings/66_string_edge_cases/
- [x] 2.26 src/tests/types/19_enum/
- [x] 2.27 src/tests/types/52_numeric_aliases/
- [x] 2.28 src/tests/types/63_nullable_value_types/
- [x] 2.29 src/tests/types/64_type_conversions/

## 阶段 3: 测试发现器双模式适配

约定：
- **Dir mode（保留）**：`<cat>/<name>/source.z42` + `<cat>/<name>/source.zbc`，case_id = `<name>`
- **Flat mode（新增）**：`<cat>/<name>.z42` + `<cat>/<name>.zbc`，case_id = `<name>`

迭代器抽象：枚举 `(case_id, source_z42_path, zbc_path)` 三元组（C# 端等价于 `(name, sourceFile, dirOrNull)`），下游 sidecar 加载（features.toml / expected_output.txt 等）按 source 路径推导，flat 模式下 sidecar 不存在视为缺省。

- [x] 3.1 [scripts/test-vm.sh](../../scripts/test-vm.sh) glob 与循环改造（dir + flat 双枚举，artifact path 推导分支）
- [x] 3.2 [scripts/regen-golden-tests.sh](../../scripts/regen-golden-tests.sh) 同款双模式（compile output 路径要写到 `<name>.zbc` vs `<name>/source.zbc`）
- [x] 3.3 [scripts/test-dist.sh](../../scripts/test-dist.sh) 同款双模式（与 test-vm.sh 共享语义）
- [x] 3.4 **[src/compiler/z42.Tests/GoldenTests.cs](../../src/compiler/z42.Tests/GoldenTests.cs) 测试发现器双模式**（Scope 扩展 2026-05-08）—— `DiscoverCases()` 除枚举含 `source.z42` 的目录外，新增枚举 `<cat>/*.z42` 平铺文件；测试方法 `(name, dir)` 改 `(name, sourceFile, dir)`，dir-依赖 sidecar（features.toml / emit_format.txt / expected_output.txt / expected.zasm / expected_error.txt）按 source 路径推导 dir，flat 模式 dir = null 时 sidecar 取缺省值
- [x] 3.5 [.gitignore](../../.gitignore) 确认 `*.zbc` / `*.zasm` / `*.z42ir.json` 通配已覆盖 flat 模式（应该已经 OK，确认即可）
- [x] 3.6 [scripts/test-cross-zpkg.sh](../../scripts/test-cross-zpkg.sh) 检查（cross-zpkg 不在范围内，预期无需改）

## 阶段 4: 文档

- [x] 4.1 [src/tests/README.md](../../src/tests/README.md) "用例文件约定" 表加一行 flat 模式说明；"添加新测试" 段说明何时用 flat / 何时用 dir

## 阶段 5: 验证

- [x] 5.1 `./scripts/regen-golden-tests.sh --no-stdlib` 全部 156+ case 重新编译产出（flat 模式产 `<name>.zbc` / dir 模式产 `<name>/source.zbc`）
- [x] 5.2 `./scripts/test-vm.sh` 双模式（interp + jit）全绿
- [x] 5.3 `dotnet test src/compiler/z42.slnx` 全绿
- [x] 5.4 `git status` 检查无残留空目录或意外 untracked

## Scope

| 文件 / 路径 | 类型 | 说明 |
|---|---|---|
| `src/tests/operators/36_parse_and_string_static/expected.txt` | DELETE | 空，无 consumer |
| `src/tests/operators/37_postfix_expr_context/expected.txt` | DELETE | 同上 |
| `src/tests/<29 个 case>/source.z42` → `src/tests/<29 个 case>.z42` | RENAME | 见阶段 2 列表 |
| `src/tests/<29 个 case>/`（空目录） | DELETE | git mv 后清理 |
| `scripts/test-vm.sh` | MODIFY | dual-mode 枚举 |
| `scripts/regen-golden-tests.sh` | MODIFY | dual-mode 枚举 + 输出路径 |
| `scripts/test-dist.sh` | MODIFY | dual-mode 枚举（与 test-vm 共享） |
| `src/compiler/z42.Tests/GoldenTests.cs` | MODIFY | dual-mode 测试发现器（Scope 扩展 2026-05-08） |
| `scripts/test-cross-zpkg.sh` | MODIFY (if needed) | 视检查结果 |
| `.gitignore` | MODIFY (if needed) | 视检查结果 |
| `src/tests/README.md` | MODIFY | 文件约定表 + 添加新测试段 |

**只读引用：**
- `src/libraries/<lib>/tests/<name>/` — 只读，本批不动（库本地 tests 沿用 dir 模式）

## 备注

- 不动 `src/libraries/<lib>/tests/`（库本地测试沿用 dir，独立批次决定是否迁移）
- 不改 `NN_` 数字前缀（独立批次）
- 不动 errors/parse/cross-zpkg（用不同执行路径）
