# Tasks: Drop NN_ Numeric Prefix from Test Names

> 状态：🟢 已完成 | 完成：2026-05-08 | 创建：2026-05-08
> 类型：refactor（最小化模式）

**变更说明：** 移除所有 z42 测试 case 文件/目录的 NN_ 数字前缀（`04_arrays.z42` → `arrays.z42`，`07_class_basic/` → `class_basic/`）。跨 category 编号已乱、含义模糊；剥离后命名更简洁。同步更新 active 代码与文档中的引用。

**原因：** 调研报告（2026-05-08）将 NN_ 前缀标识为"历史遗留"。此为收尾批次，让命名约定彻底简化。

**保留：** archived spec 文档（历史不可变）；提交 history 中的 NN_ 名（git rename 检测保留追溯）。

**Scope 警告（2026-05-08）：** 初始估计 "2 active doc refs"。重新搜索后实际：
- `src/runtime/tests/zbc_compat.rs` — ~6 处代码 fixture path
- `docs/design/generics.md` — ~8 处特性记号引用
- `docs/design/gc-handle.md` — 2 处全路径
- `docs/design/static-abstract-interface.md` — 2 处特性记号
- `.claude/settings.json` — 2-3 处权限白名单全路径

User 确认扩展 Scope 继续。

## 进度概览

- [x] 阶段 1: 检查冲突（无）
- [x] 阶段 2: git mv 全部 ~149 个文件/目录
- [x] 阶段 3: 更新代码 / 文档 / settings 引用
- [x] 阶段 4: 验证

## 阶段 1: 冲突检查

- [x] 1.1 已确认：strip 后无 within-category 重名

## 阶段 2: 重命名

- [x] 2.1 src/tests/ flat .z42 文件（~36）：`<cat>/NN_<name>.z42` → `<cat>/<name>.z42`
- [x] 2.2 src/tests/ dir 形态（~95）：`<cat>/NN_<name>/` → `<cat>/<name>/`
- [x] 2.3 src/libraries/ dir 形态（~20）：`<lib>/tests/NN_<name>/` → `<lib>/tests/<name>/`

## 阶段 3: 引用更新

- [x] 3.1 [src/runtime/tests/zbc_compat.rs](../../src/runtime/tests/zbc_compat.rs)：`basic/01_hello` → `basic/hello`、`classes/07_class_basic` → `classes/class_basic`，及 doc comment 中相关引用
- [x] 3.2 [docs/design/generics.md](../../docs/design/generics.md)：`82_short_circuit` / `89_static_abstract_operator` / `80_stdlib_arraylist` / `81_stdlib_hashmap` / `83_foreach_user_class` / `18_list` / `20_dict` / `40_list_operations` / `76_generic_list` → 去 NN_
- [x] 3.3 [docs/design/gc-handle.md](../../docs/design/gc-handle.md)：`src/tests/gc/113_gc_handle/` / `114_gc_stats/` → 去 NN_
- [x] 3.4 [docs/design/static-abstract-interface.md](../../docs/design/static-abstract-interface.md)：`87_generic_inumber` / `88_operator_overload` → 去 NN_
- [x] 3.5 [.claude/settings.json](../../.claude/settings.json)：`src/tests/parse/01_hello` / `src/tests/cross-zpkg/01_impl_propagation/*` / `src/libraries/z42.math/tests/golden/15_math/source.zbc`（后者含 stale `/golden/` 中间目录，一并修正） → 去 NN_

## 阶段 4: 验证

- [x] 4.1 `./scripts/regen-golden-tests.sh --no-stdlib` 全绿
- [x] 4.2 `./scripts/test-vm.sh` interp + jit 全绿
- [x] 4.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿
- [x] 4.4 `cargo test --manifest-path src/runtime/Cargo.toml` 全绿（zbc_compat fixture loader）

## Scope

| 路径 | 类型 | 说明 |
|---|---|---|
| 36 个 src/tests/`<cat>`/NN_*.z42 | RENAME | flat |
| 95 个 src/tests/`<cat>`/NN_*/ | RENAME | dir |
| 20 个 src/libraries/`<lib>`/tests/NN_*/ | RENAME | lib dir |
| `src/runtime/tests/zbc_compat.rs` | MODIFY | path strings |
| `docs/design/generics.md` | MODIFY | 8 处特性引用 |
| `docs/design/gc-handle.md` | MODIFY | 2 处全路径 |
| `docs/design/static-abstract-interface.md` | MODIFY | 2 处特性引用 |
| `.claude/settings.json` | MODIFY | 3 处权限白名单 |

## 备注

archived spec 不动（历史记录）。本批为批量授权最后一批，结束后整体收尾。
