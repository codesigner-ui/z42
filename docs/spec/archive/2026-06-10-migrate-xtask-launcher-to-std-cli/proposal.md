# Proposal: xtask + launcher 迁移到 Std.Cli 嵌套 router

## Why

xtask 和 launcher 两个 CLI 都手撸多层 `if-else` 解析，导致：
- **子命令无 help**：`xtask build package -h` 不显示帮助而直接打包（[xtask_package.z42:20-28](../../../../scripts/xtask_package.z42) 的 `while` 静默跳过 `-h`）；`z42 run -h` / `z42 link -h` 同样无效。
- **help 文本手写、与命令树漂移**：xtask `_help()` 43 行、launcher `_help()` 各自维护，加子命令要多处同步。
- **未知 flag 处理不一致**：有的报错、有的静默吞掉。
- **命令树组织混乱**：`package` 埋在 `build` 二级（实为独立能力）；`test stdlib`/`test lib`、`bench stdlib`/`bench lib` 重复别名；`feature-matrix`（CI 编译矩阵验证，不产 artifact）挂在 `build` 下。

前置依赖 `Std.Cli` 嵌套 router（`AddRouter`/`Resolve`/`CommandResolution`）已于 2026-06-10 落地（archive/2026-06-10-add-cli-nested-subcommands）。本变更把两个 CLI 的消费端迁移过去，根治上述问题。

## What Changes

### 命令树重组（用户可见）

| 现状 | 重组后 |
|------|--------|
| `xtask build package [release\|debug] [--rid R] [--variant V]` | **`xtask package [release\|debug] [--rid R] [--variant V]`**（提顶层）|
| `xtask build feature-matrix` | **`xtask feature-matrix`**（提顶层；CI 验证语义，非 build artifact）|
| `xtask test stdlib` + `xtask test lib`（别名）| **`xtask test stdlib [lib]`**（删 `lib` 别名）|
| `xtask bench stdlib` + `xtask bench lib`（别名）| **`xtask bench stdlib [lib]`**（删 `lib` 别名）|
| 其余命令结构不变 | 不变 |

### 机制迁移（两个 CLI）

- xtask Main + 各级 dispatch 改用 `Std.Cli.SubcommandRouter` 嵌套树 + `Resolve`；每个 leaf 声明 `ArgParser`（flag/option/positional），handler 从 `ParseResult` 读参数。
- launcher Main + 各命令同样迁移；补 `z42 run -h` / `link -h` / `which -h` / `apphost build -h` 等每层 help。
- **删除手写 `_help()`**（xtask + launcher）→ 改用库自动生成的 `HelpText()`（消除漂移）。
- 未知子命令/未知 flag → 统一经 `Resolve` 三态（`IsUnknown` / `CliException`）一致报错。

### 不变量（保持行为）

- 每个命令的实际行为、退出码、env-var 读取（`Z42_TEST_CHANGED_BASE` 等）、`--` app-args 透传不变。
- `z42 <app.zpkg>` apphost 简写、`test` 空参=全量 gate、`test --flag`=全量 gate 等隐式行为保留。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/xtask.z42` | MODIFY | Main → Std.Cli router 树；`package`/`feature-matrix` 提顶层；删 `test lib` 别名；删手写 `_help()` |
| `scripts/xtask_package.z42` | MODIFY | `_buildPackage` 从 `ParseResult` 读 `--rid`/`--variant`/`profile`（不再 `i=2` 扫描）|
| `scripts/xtask_test.z42` | MODIFY | `_testVm`（`--no-rebuild`/`--jobs`/mode）、`_testLib`（`[lib]`）、`_testCrossZpkg`/`_testDist`（mode）从 ParseResult 读 |
| `scripts/xtask_test_changed.z42` | MODIFY | `_testChanged` 参数解析（`--dry-run`/base-ref）改 ParseResult；保留 env 后备 |
| `scripts/xtask_install.z42` | MODIFY | `_depsInstall`（`--os`/`--check`/`--drift`/`--print-env`/`--force`/`node`/`android-emulator`）改 ParseResult |
| `scripts/xtask_bench.z42` | MODIFY | `_bench`（`--quick`/`stdlib`）+ `_benchDiff`（`--current`/`--baseline`/`--threshold-*`/`--quiet`）改 ParseResult |
| `src/toolchain/launcher/core/launcher.z42` | MODIFY | Main → Std.Cli router；各命令 ArgParser（`--runtime`/`--as`/positional）；删手写 `_help()`；每层 help |
| `src/toolchain/launcher/core/apphost.z42` | MODIFY | `Apphost.Build` 改 ArgParser（`build` 子命令 + `--out` + positional）；`apphost build -h` |
| `src/toolchain/launcher/core/launcher.z42.toml` | MODIFY（若需）| 加 `z42.cli` 依赖（launcher 接 Std.Cli）|
| `scripts/xtask.z42.toml` | MODIFY（若需）| 加 `z42.cli` 依赖（若 xtask 尚未依赖）|
| `.github/workflows/ci.yml` | MODIFY | `xtask -- build package` → `xtask -- package`（4 处：270/576/678/776 行）|
| `.github/workflows/release.yml` | MODIFY | `xtask -- build package` → `xtask -- package`（151 行）|
| `docs/workflow/ci.md` | MODIFY | `test lib` → `test stdlib`（删别名同步）|
| `docs/workflow/testing/changed-only.md` | MODIFY | `test lib` → `test stdlib` |
| `docs/design/compiler/project.md` 或 toolchain CLI doc | MODIFY | 同步新命令树（package 顶层等）|
| `src/toolchain/launcher/core/README.md` | MODIFY | launcher 核心文件表 + Std.Cli 接入 |
| `scripts/README.md`（若有命令树清单）| MODIFY（若需）| xtask 命令树同步 |

**只读引用**（不修改）：
- `src/libraries/z42.cli/` — `Std.Cli` API（ArgParser/SubcommandRouter/Resolve/CommandResolution）
- `scripts/xtask_common.z42`、各 `_package_*`/`_test_*`/`_install_android` 委派实现 — 内部逻辑不动，仅上游传参方式变

## Out of Scope

- **重建 apphost bundled z42vm 二进制**（让 repo-root `./xtask` 恢复可用）—— 属工具链维护/版本 bump 联动（add-reflection-static-fields 0.15 在途），非本 CLI 迁移；本变更验证经 fresh runtime vm 旁路。
- subcommand 别名机制（`co`=`commit`）、缩写匹配、跨子命令全局 flag —— `Std.Cli` Deferred。
- 去 bash 编排（migrate-scripts-to-z42 的关注点）—— 正交，不在本变更。
- 命令的实际行为/算法改动 —— 仅迁移解析层，handler 逻辑不动。

## Open Questions（已查证，gate 确认）

- [x] `feature-matrix` 提顶层 —— CI 的 feature-matrix job **不经 xtask**（直接 `cargo build --features …`），提顶层零破坏。采用顶层 `xtask feature-matrix`（不建单成员 `check` 组）。
- [x] `build package` CI 调用点 —— 共 **5 处**（ci.yml 270/576/678/776 + release.yml 151），提顶层须同步改为 `xtask -- package`（已入 Scope）。
- [x] `test lib` 别名 —— CI yml **不用**（用 `test all`/`test vm`/`test cross-zpkg`），仅 2 个 doc 引用。删 `lib`/`bench lib` 别名 + 改 2 doc。
- **兼容测试向量（行为不变验证）**：CI 现有调用形式必须解析不变 ——
  `build stdlib` / `deps check` / `test all` / `regen --no-stdlib` /
  `test vm jit --jobs=4` / `test cross-zpkg jit` / `bench` / `bench --quick` /
  `bench --diff --current … --baseline … --threshold-* … --quiet` /
  `package release [--rid R]`。实施期逐条本地验证。
