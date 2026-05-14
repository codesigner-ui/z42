# Tasks: Std.Platform + Std.OperatingSystem

> 状态：🟢 已完成 | 创建：2026-05-14 | 完成：2026-05-15
> 类型：feat（新增 stdlib + corelib builtins，无 IR/VM/lang 变更）
> 关联：[proposal.md](proposal.md) + [design.md](design.md) + [specs/platform-os/spec.md](specs/platform-os/spec.md)

## 进度概览

- [ ] 阶段 1: Rust corelib — `platform.rs` (6 builtins) + 单测
- [ ] 阶段 2: Rust corelib — `system.rs` (7 builtins，含 hostname / uname 跨平台分支) + 单测
- [ ] 阶段 3: Rust corelib — `fs.rs` 追加 `__env_unset` / `__env_vars` + 单测
- [ ] 阶段 4: Rust corelib/mod.rs — 注册 15 个新 builtins
- [ ] 阶段 5: z42 stdlib — `Platform.z42` + `OSKind` / `ArchKind` 常量
- [ ] 阶段 6: z42 stdlib — `OperatingSystem.z42`
- [ ] 阶段 7: z42 stdlib — `Environment.z42` 扩展（UnsetEnv / GetAllEnv）
- [ ] 阶段 8: z42 集成测试 — `tests/platform.z42` + `tests/environment_extra.z42`
- [ ] 阶段 9: 文档 — `docs/design/runtime/stdlib-platform.md` + IncrementalBuildIntegrationTests 计数同步
- [ ] 阶段 10: GREEN + commit + 归档

## 阶段 1: `platform.rs`

- [ ] 1.1 NEW `src/runtime/src/corelib/platform.rs`
  - 6 functions：`builtin_platform_os` / `_arch` / `_family` / `_os_kind` / `_arch_kind`
  - 走 `std::env::consts::{OS, ARCH, FAMILY}`
  - OSKind / ArchKind match 表 — **与 z42.io Platform.z42 OSKind/ArchKind 常量值
    严格对齐**（design.md decision 4）
- [ ] 1.2 NEW `src/runtime/src/corelib/platform_tests.rs`
  - `os/arch/family` 在当前编译 target 上的值非空
  - `os_kind` 在 linux / macos / windows / wasm 各分支按 cfg 验证返回值
- [ ] 1.3 `cargo test --lib corelib::platform` 全绿

## 阶段 2: `system.rs`

- [ ] 2.1 NEW `src/runtime/src/corelib/system.rs`
  - `builtin_system_pid` — `std::process::id()`
  - `builtin_system_exe_path` — `std::env::current_exe()`；错误→`""`
  - `builtin_system_cwd` / `_set_cwd` — `std::env::current_dir` / `set_current_dir`
  - `builtin_system_hostname` — `#[cfg(unix)]` 用 `libc::gethostname`；
    `#[cfg(windows)]` 用 `winapi::um::winbase::GetComputerNameW`；wasm/其他 → `""`
  - `builtin_system_cpu_count` — `std::thread::available_parallelism()`，错误→1
  - `builtin_system_os_version` — unix `libc::uname()` 三段组合；windows
    RtlGetVersion；wasm `"wasm"`；错误→`""`
- [ ] 2.2 Windows `winapi` 依赖处理：检查 z42 runtime Cargo.toml；若无则加
  `winapi = { version = "0.3", features = ["sysinfoapi", "winbase"], optional = false, target_arch = ... }` —— **首次实施时确认是否引入；不引入则用
  GetEnvironmentVariableW 间接拿 COMPUTERNAME / OS** 字段（fallback path）
- [ ] 2.3 NEW `src/runtime/src/corelib/system_tests.rs`
  - PID > 0、CpuCount >= 1、ExecutablePath ends with "z42-test-runner" or similar
  - cwd 闭环：取值 → set_current_dir(tmp) → 取值 → 应变化
  - hostname 在 Linux/macOS CI 上非空
- [ ] 2.4 `cargo test --lib corelib::system` 全绿

## 阶段 3: `fs.rs` 扩展

- [ ] 3.1 MODIFY `src/runtime/src/corelib/fs.rs`：追加 `builtin_env_unset` 和 `builtin_env_vars`
- [ ] 3.2 单测：在 `corelib::tests` 或 fs_tests.rs 加用例
  - `__env_unset` 后 `__env_get` 返回 Null
  - `__env_vars` 数组含已设置的 key

## 阶段 4: `mod.rs` 注册

- [ ] 4.1 MODIFY `src/runtime/src/corelib/mod.rs`
  - 加 `pub mod platform;` + `pub mod system;`
  - BUILTINS 表追加 15 条 (`__platform_*` x5, `__system_*` x7, `__env_unset` /
    `__env_vars` x2 — 后两条在 fs section)
- [ ] 4.2 `cargo build` 通过

## 阶段 5: `Platform.z42`

- [ ] 5.1 NEW `src/libraries/z42.core/src/Platform.z42` (z42.core advertises `namespace Std`; z42.io only advertises `Std.IO` so `Std.Platform` must live in z42.core)
  - `Std.Platform` static class：5 extern + 8 predicates（design.md 完整版）
  - `Std.OSKind` static class：8 整数常量
  - `Std.ArchKind` static class：5 整数常量
- [ ] 5.2 `./scripts/build-stdlib.sh` z42.io 构建通过

## 阶段 6: `OperatingSystem.z42`

- [ ] 6.1 NEW `src/libraries/z42.core/src/OperatingSystem.z42`
  - `Std.OperatingSystem` static class：7 extern
- [ ] 6.2 stdlib 构建通过

## 阶段 7: `Environment.z42` 扩展

- [ ] 7.1 MODIFY `src/libraries/z42.io/src/Environment.z42`
  - 加 `[Native("__env_unset")] UnsetEnvironmentVariable(string name)`
  - 加 `[Native("__env_vars")] GetEnvironmentVariables() -> string[]`
- [ ] 7.2 stdlib 构建通过

## 阶段 8: z42 集成测试

- [ ] 8.1 NEW `src/libraries/z42.io/tests/platform.z42`
  - `[Test] test_os_string_is_known()` — `Platform.OS()` ∈ 已知集
  - `[Test] test_os_kind_matches_predicate()` — `IsLinux() ⇔ OSKindValue == OSKind.Linux`，etc.
  - `[Test] test_arch_known()` — `Arch()` 非空字符串
  - `[Test] test_family_unix_or_windows()` — `Family()` 三选一
  - `[Test] test_is_unix_consistent()` — unix family 必触发 `IsUnix() == true`
- [ ] 8.2 NEW `src/libraries/z42.io/tests/operating_system.z42`
  - `[Test] test_pid_positive()` — `CurrentPid() > 0`
  - `[Test] test_cpu_count_at_least_one()` — `CpuCount() >= 1`
  - `[Test] test_cwd_changes_after_set()` — set_cwd 后 cwd 变化
  - `[Test] test_hostname_non_empty()` — Hostname 非空（CI assumption）
  - `[Test] test_os_version_non_empty_or_empty()` — 返回 string 即可（不强校验内容）
- [ ] 8.3 NEW `src/libraries/z42.io/tests/environment_extra.z42`
  - `[Test] test_unset_after_set()` — set + unset → get == null
  - `[Test] test_unset_nonexistent_is_noop()` — 不抛
  - `[Test] test_get_all_contains_set_key()` — 平铺数组里含 `"K=V"`
  - `[Test] test_get_all_every_item_has_equals()` — 每项含 `=`
- [ ] 8.4 `./scripts/test-stdlib.sh z42.io` 通过

## 阶段 9: 文档 + 计数同步

- [ ] 9.1 NEW `docs/design/runtime/stdlib-platform.md`
  - 架构图 + design.md decision 1-6 的固化版
  - 跨平台差异表（hostname / uname 的 Windows/Wasm 路径）
- [ ] 9.2 MODIFY `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs`
  - z42.core 文件数从 49 → **52**（+ Platform.z42 + OperatingSystem.z42 + ArchKind.z42；ArchKind 因 z42 编译器 bug 拆单独文件）
- [ ] 9.3 grep 验证文档同步：搜 "未支持 OS 识别" 等过时表述是否还在 docs/

## 阶段 10: GREEN + 归档

- [ ] 10.1 完整运行：
  - `dotnet build src/compiler/z42.slnx`
  - `cargo build --manifest-path src/runtime/Cargo.toml`
  - `dotnet test --filter !~Incremental&!~GoldenTests`
  - `cargo test --lib`
  - `./scripts/test-stdlib.sh z42.io`
- [ ] 10.2 tasks.md 状态 → 🟢 已完成 + 完成日期
- [ ] 10.3 移动 `docs/spec/changes/add-platform-os-stdlib/` → `docs/spec/archive/YYYY-MM-DD-add-platform-os-stdlib/`
- [ ] 10.4 commit: `feat(stdlib+vm): add Std.Platform / Std.OperatingSystem — cross-platform OS detection + system info`
- [ ] 10.5 push origin main

## 备注

### 不解决的问题（follow-up spec 处理）

- **Linux 发行版识别**（`/etc/os-release` 解析）—— 独立 spec，需要 `Std.IO.File.ReadAllText` + 简单解析
- **物理 CPU 数 / NUMA 拓扑**—— 独立 spec，比 logical CPU count 复杂
- **Map<string, string> 形态的 GetEnvironmentVariables overload**—— 等通用 Map<K,V> marshal 稳定
- **OS 版本结构化解析**（`SemVer` / `Std.Time.DateTime` 风格）—— 等 SemVer stdlib

### 风险监控

- **z42.io 文件数从 13 → 15**：`IncrementalBuildIntegrationTests` 必须同步，否则 GREEN 阶段失败
- **windows winapi dep**：若 z42 runtime Cargo.toml 之前无 winapi 直接依赖，本 spec
  是第一次引入；首次实施时确认 dep 增加是否需要单独决策（design.md decision 已倾向不引入第三方）
