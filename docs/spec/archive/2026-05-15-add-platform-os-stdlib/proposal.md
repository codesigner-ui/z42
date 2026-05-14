# Proposal: Std.Platform + Std.OperatingSystem stdlib

## Why

跨平台脚本类代码（迁移 `scripts/*.sh` → z42、CI 工具、宿主集成层）需要在
z42 源码里识别 OS / 架构、读取 hostname / CPU 数 / 进程信息。z42 当前
`Std.IO.Environment` 只暴露 `GetEnvironmentVariable` / `Set...` /
`GetCommandLineArgs` / `Exit`；缺**全部**平台 / 系统信息 API。

API 形态参考 **Rust `std::env::consts`/`std::env`/`std::process`** + **.NET
`System.OperatingSystem` / `System.Environment` / `System.Runtime.InteropServices.RuntimeInformation`**：两边都收敛在 **string 常量
+ 类型化谓词 + 数字常量**三种形态共存的模式。

**不做会怎样**：脚本只能 `Std.Process` 子调 `uname` 解析 stdout 来识别平台；
进程 PID / CPU 数 等更是不可达。z42 stdlib 永远停在"能写 hello world"
但写不了 "build script" 的位置。

## What Changes

### 新增 `Std.Platform` 类（z42.io 包内，namespace `Std`）

**String 形态**（Rust-style，匹配 `std::env::consts::OS` 的值域）：

- `Platform.OS()` → `"linux" | "macos" | "windows" | "android" | "ios" | "wasm" | "freebsd" | "unknown"`
- `Platform.Arch()` → `"x86_64" | "aarch64" | "wasm32" | "x86" | "unknown"`
- `Platform.Family()` → `"unix" | "windows" | "wasm" | "unknown"`

**Enum 形态**（.NET-style，类型安全分支）：

- `Platform.OSKindValue()` → `int`（对应 `OSKind.*` 常量）
- `Platform.ArchKindValue()` → `int`（对应 `ArchKind.*` 常量）
- `OSKind` 静态类：`Unknown=0, Linux=1, MacOS=2, Windows=3, Android=4, IOS=5, Wasm=6, FreeBSD=7`
- `ArchKind` 静态类：`Unknown=0, X86_64=1, Arm64=2, Wasm32=3, X86=4`

**便利谓词**（.NET `OperatingSystem.IsXxx` 风格）：

- `Platform.IsLinux()` / `IsMacOS()` / `IsWindows()` / `IsAndroid()` / `IsIOS()` /
  `IsWasm()` / `IsFreeBSD()` / `IsUnix()`

### 新增 `Std.OperatingSystem` 类（z42.io 包内，namespace `Std`）

参考 .NET `System.Environment` 的进程 / 主机信息字段 + .NET
`System.OperatingSystem.Version`：

- `OperatingSystem.CurrentPid()` — 当前进程 pid（Rust `std::process::id()` / .NET `Environment.ProcessId`）
- `OperatingSystem.ExecutablePath()` — 当前可执行文件路径（Rust `std::env::current_exe()` / .NET `Environment.ProcessPath`）
- `OperatingSystem.CurrentDirectory()` — 当前工作目录（Rust `std::env::current_dir()` / .NET `Environment.CurrentDirectory`）
- `OperatingSystem.SetCurrentDirectory(path)` — 切换 cwd（Rust `std::env::set_current_dir`）
- `OperatingSystem.Hostname()` — 主机名（libc `gethostname` / Windows `GetComputerNameW` / .NET `Environment.MachineName`）
- `OperatingSystem.CpuCount()` — 逻辑 CPU 数（Rust `std::thread::available_parallelism()` / .NET `Environment.ProcessorCount`）
- `OperatingSystem.OsVersion()` — OS 版本字符串，如 `"macOS 14.3"` / `"Linux 6.5.0"`（libc `uname` / Windows `RtlGetVersion`）；无法获取时返回 `""`

### 扩展 `Std.IO.Environment`（现有 class）

- `Environment.UnsetEnvironmentVariable(name)` — Rust `std::env::remove_var` / .NET `SetEnvironmentVariable(name, null)`
- `Environment.GetEnvironmentVariables()` → `string[]` — 平铺 `"KEY=VALUE"` 数组（z42 暂无稳定 Map marshal 路径；调用方 `.Split('=')` 即可）

### Rust 侧新增 15 个 corelib builtins

```
__platform_os         __platform_arch       __platform_family
__platform_os_kind    __platform_arch_kind
__system_pid          __system_exe_path
__system_cwd          __system_set_cwd
__system_hostname     __system_cpu_count    __system_os_version
__env_unset           __env_vars
```

实现走 `std::env::consts::*` + `std::process::id()` + `std::env::current_exe/current_dir/set_current_dir` + `std::thread::available_parallelism()` +
直接 `libc::gethostname` / `libc::uname`（unix）和 winapi 等价物（windows）。
**不引入 hostname / os_info crate**——保持 runtime 依赖最小。

## Decisions（User 确认 2026-05-14）

1. **类名 `Std.System` → `Std.OperatingSystem`**：避免和 .NET `System.*` 整片命名
   空间冲突感；`OperatingSystem` 直接呼应 .NET `System.OperatingSystem` 类型名。
2. **String + Enum 双形态共存**：脚本党用 `if (Platform.OS() == "macos")` 直接写，
   类型安全党用 `if (Platform.OSKindValue() == OSKind.MacOS)`。两种代码风格都能跑。
3. **`OsVersion()` 拿不到时返回 `""`**：脚本场景下"我只是想知道大概版本"返回空串比
   try/catch 轻量得多。
4. **`Hostname` 走 libc/winapi 直接调**：不引入 `hostname` crate，runtime 依赖最小。

## Scope（允许改动的文件）

| 文件路径 | 变更 | 说明 |
|---|---|---|
| `src/libraries/z42.core/src/Platform.z42` | NEW | `Std.Platform` + `Std.OSKind`（z42 编译器 bug：同文件第二个含 static field 的 static class 静默失败，ArchKind 单独拆出） |
| `src/libraries/z42.core/src/ArchKind.z42` | NEW | `Std.ArchKind` 静态常量（X64 / Arm64 / Wasm / X86 / Unknown），单独成文件以绕过编译器 bug |
| `src/libraries/z42.core/src/OperatingSystem.z42` | NEW | `Std.OperatingSystem` class |
| `src/libraries/z42.io/src/Environment.z42` | MODIFY | 增加 `UnsetEnvironmentVariable` + `GetEnvironmentVariables` |
| `src/runtime/src/corelib/platform.rs` | NEW | 6 个 `__platform_*` builtins |
| `src/runtime/src/corelib/system.rs` | NEW | 7 个 `__system_*` builtins |
| `src/runtime/src/corelib/platform_tests.rs` | NEW | platform builtins 单测 |
| `src/runtime/src/corelib/system_tests.rs` | NEW | system builtins 单测 |
| `src/runtime/src/corelib/fs.rs` | MODIFY | 增加 `__env_unset` + `__env_vars`（沿用现有 `__env_*` 命名空间） |
| `src/runtime/src/corelib/mod.rs` | MODIFY | dispatch_table 注册 15 个新 builtins + 2 个 `pub mod` |
| `src/libraries/z42.io/tests/platform.z42` | NEW | z42 集成测试（Platform + OperatingSystem 各 ~5 测试） |
| `src/libraries/z42.io/tests/environment_extra.z42` | NEW | UnsetEnv / EnumerateEnv 测试 |
| `docs/design/stdlib/platform.md` 或 `runtime/stdlib-platform.md` | NEW | 实现原理 + 跨平台差异表 |
| `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` | MODIFY | z42.io 文件数 13 → 15（同步 stdlib 计数断言） |

**只读引用**：
- `src/libraries/z42.io/src/{File,Path,Environment,Process}.z42` — 同包风格对齐
- `src/runtime/src/corelib/fs.rs` — 现成 `__env_*` builtin 模式

## Out of Scope

- **Mac OS 区分次版本号**（"Sonoma" / "Ventura"）：当前只返回 `"macOS X.Y"`
- **Linux 发行版识别**（`/etc/os-release`）：单独 spec
- **物理 CPU 数 vs 逻辑 CPU 数**：仅暴露 `available_parallelism` 等价值（逻辑）
- **Map<string,string> 形态的 env 列表**：等通用 marshal 路径稳定后再加重载
- **`Std.OperatingSystem.Version` 解析为结构体**：当前返回字符串；结构化版本对象等
  z42 stdlib 有 `SemVer` 类后再做

## Open Questions

无（4 个 decisions 已在上面确认）。
