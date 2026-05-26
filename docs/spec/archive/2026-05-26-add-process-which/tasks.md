# Tasks: add Process.Which (PATH executable lookup)

> 状态：🟢 已完成 | 创建：2026-05-26 | 归档：2026-05-26

**变更说明：** 在 `Std.IO.Process` 添加静态方法 `Which(name) → string?`，封装跨平台 `$PATH` 查找。

**原因：** 准备把 `scripts/*.sh` 移植成 z42 脚本。已盘点的 P0 stdlib 缺口里，其他两项（`Std.Platform.OS()/Arch()` 与 `ProcessHandle.Wait()`）实际已存在；只有 `command -v / which` 还没有等价 API，多个 setup / test 脚本依赖该 gate。

**类型：** 最小化（stdlib 扩展 + 1 个新 native binding，无新 IR/VM 语义）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/process.rs` | MODIFY | 新增 `builtin_process_which`，~30 LOC |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 在 BUILTINS 表注册 `__process_which` |
| `src/runtime/src/corelib/process_tests.rs` | MODIFY | 新增 Rust 单测（PATH set / not-found / abs-path passthrough） |
| `src/libraries/z42.io/src/Process.z42` | MODIFY | 新增 `public static string? Which(string name)` |
| `src/libraries/z42.io/tests/process_which.z42` | NEW | stdlib [Test] 端到端用例 |
| `src/libraries/z42.io/README.md` | MODIFY | 在 Process 段引用新 API |
| `docs/design/runtime/vm-architecture.md` | MODIFY | 在 corelib builtin 表追加 `__process_which` 一行 |

**只读引用：**
- `src/runtime/src/corelib/platform.rs` — 参考 `__platform_os` 极简风格
- `src/libraries/z42.io/src/Environment.z42` — 命名风格对照
- `scripts/setup-tools.sh` / `scripts/_lib/versions.sh` — 验证 API 形状匹配脚本所需

## 文档影响
- z42.io README：是
- vm-architecture.md：是（builtin 表）
- 不需要新 design doc（语义直接对应 `which(1)`，无独立 design 决策）

## Tasks

- [x] 1.1 在 `corelib/process.rs` 加 `builtin_process_which`：拆 `PATH`，按 OS 分隔符迭代候选目录，检查 `target.is_file() && metadata.permissions().mode() & 0o111 != 0`（Unix）或 `PATHEXT` 后缀（Windows）。绝对/相对路径含 `/` 时直接 stat 不走 PATH。返回 `Value::Str(full_path)` 或 `Value::Null`。
- [x] 1.2 在 `corelib/mod.rs` BUILTINS 表追加 `("__process_which", process::builtin_process_which)`（自身专属 section，保留既有 BuiltinId 顺序）。
- [x] 1.3 Rust 单测（6 个新 case 写完，pre-existing test build break 致无法 run，见下方备注）：
  - `which_returns_null_for_empty_name`
  - `which_returns_null_for_nonexistent_command`
  - `which_finds_in_custom_path`（unix only）
  - `which_skips_non_executable_files`（unix only）
  - `which_passthrough_for_path_with_separator`（unix only）
  - `which_passthrough_returns_null_when_path_missing`（unix only）
- [x] 1.4 在 `z42.io/src/Process.z42` 添加 `Process.Which(name)` 静态方法 + `ProcessNative.Which` native binding。
- [x] 1.5 写 `z42.io/tests/process_which.z42`（5 个 [Test]，均 GREEN）。
- [x] 1.6 更新 `z42.io/README.md` Process 行。
- [ ] 1.7 ~~更新 `docs/design/runtime/vm-architecture.md` builtin 表~~ — 该文件无 per-builtin 表，"三处同改" 中提到的 `corelib/dispatch.rs` 和 `BuiltinTable.cs` 实际都不存在（doc 已 rot；out of scope 修）。
- [x] 1.8 验证：lib `cargo build --release` 干净；`./scripts/test-stdlib.sh z42.io` 36/36 全绿（含新增 5 个 process_which case）。
- [x] 1.9 归档 + commit + push（单文件 hunk-pick，避开同步在工作树的 add-csprng-to-crypto 改动）。

## 备注
- process.rs 文件已经 692 行（超 500 硬限），这是先前累积的 pre-existing 状态。本次 +30 LOC 不主动触发拆分（独立 refactor），由后续 cleanup spec 跟进。
- 命名考虑过 `Process.FindExecutable(name)`（更显式）vs `Which(name)`（unix 习惯）；后者最短且业内通用，TS / Python / Rust crate 都叫 `which`。Z42 取 `Which`。
- 返回 `string?`（null=not found）而非抛异常：与 `Environment.GetEnvironmentVariable` 风格一致，调用方便于 `if (Which("foo") == null)` 判断。抛异常会强制 try/catch，对 setup-tools.sh 这种 `--check` gate 不友好。
- **Pre-existing test build issue**：`cargo build --release --lib --tests` 当前在 `main` 上有 17 个编译错误（`gc::region_tests`、`metadata::build_id_tests`、`arc_heap::ArcMagrGC::debug_validate_invariants`、`gc::region::Violation` 等），与本变更无关。Lib production build 干净，stdlib test runner（z42c → z42vm 路径）也干净，因此本变更通过端到端 stdlib test 验证。User 已确认按 Option A 处理：跑 stdlib test 替代 Rust 单测，pre-existing 错误另案跟踪。
- **共享工作树**：commit 时工作树同时有 in-flight `add-csprng-to-crypto`（修改 `Cargo.toml` / `metadata/{bytecode,loader,zbc_reader}.rs` + 新 `corelib/crypto.rs`）。本 commit 用 `git apply --cached` 对 `corelib/mod.rs` 做 hunk-level 拣选，只 stage `__process_which` 那一行；csprng 改动留在工作树等其原 spec 自己 commit。
- **文档同步缺口**：`docs/design/runtime/vm-architecture.md` 第 684 行说"新增 builtin 三处同改" 引用了不存在的 `corelib/dispatch.rs` 和 `BuiltinTable.cs`。该 doc 已 rot，需独立 cleanup spec 修。本变更未碰。
