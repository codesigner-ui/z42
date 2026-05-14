# Spec: Std.Platform + Std.OperatingSystem

## ADDED Requirements

### Requirement: Std.Platform — OS / Arch identity

#### Scenario: OS string matches Rust std::env::consts::OS

- **WHEN** z42 用户调 `Platform.OS()`
- **THEN** 返回 Rust `std::env::consts::OS` 的原值（`"linux"` / `"macos"` /
  `"windows"` / `"android"` / `"ios"` / `"wasm"` / `"freebsd"` / …）
- **AND** 在 linux x86_64 CI 上必返回 `"linux"`
- **AND** 在 macos arm64 CI 上必返回 `"macos"`

#### Scenario: Arch string covers common targets

- **WHEN** z42 用户调 `Platform.Arch()`
- **THEN** 返回 `"x86_64"` / `"aarch64"` / `"wasm32"` / `"x86"` 之一（其他归并到原值）
- **AND** 在 macos arm64 CI 上必返回 `"aarch64"`
- **AND** 注意：`Platform.Arch()` 字符串保留 Rust target-triple 拼写；
  `ArchKind.*` 常量名采用 .NET 风格（`X64` / `Arm64` / `Wasm` / `X86`）

#### Scenario: OSKindValue / ArchKindValue 数字常量

- **WHEN** z42 用户调 `Platform.OSKindValue()`
- **THEN** 返回 `OSKind.*` 常量之一
- **AND** 不识别的 OS 归并到 `OSKind.Unknown == 0`
- **AND** 在 linux 上返回 `OSKind.Linux == 1`
- **AND** 在 macos 上返回 `OSKind.MacOS == 2`

#### Scenario: 便利谓词与 OSKindValue 自洽

- **WHEN** `Platform.IsLinux()` 返回 `true`
- **THEN** `Platform.OSKindValue() == OSKind.Linux`
- **AND** 其他 `Platform.IsXxx()` 都返回 `false`
- **AND** `Platform.IsUnix()` 在 Linux / macOS / iOS / Android / FreeBSD 上为 `true`

#### Scenario: Family 字符串区分 unix / windows / wasm

- **WHEN** z42 用户调 `Platform.Family()`
- **THEN** 在 Linux / macOS 上返回 `"unix"`
- **AND** 在 Windows 上返回 `"windows"`
- **AND** 在 wasm target 上返回 `"wasm"`

### Requirement: Std.OperatingSystem — 进程 + 主机信息

#### Scenario: CurrentPid 返回正整数

- **WHEN** z42 用户调 `OperatingSystem.CurrentPid()`
- **THEN** 返回值 `> 0`
- **AND** 多次调用同一进程内一致

#### Scenario: ExecutablePath 返回可执行文件路径

- **WHEN** z42 用户调 `OperatingSystem.ExecutablePath()`
- **THEN** 在主流平台（linux / macos）返回 absolute path 字符串
- **AND** 路径以 `/` 起始（unix）或 `<drive>:\` 起始（windows）
- **AND** 拿不到时返回 `""`（不抛异常）

#### Scenario: CurrentDirectory + SetCurrentDirectory 闭环

- **WHEN** z42 用户调 `OperatingSystem.CurrentDirectory()` 得到 `dir1`
- **AND** 调 `OperatingSystem.SetCurrentDirectory("/tmp")`
- **AND** 再调 `OperatingSystem.CurrentDirectory()` 得到 `dir2`
- **THEN** `dir2` 等于 `/tmp` 或 `/private/tmp`（macOS symlink）
- **AND** `dir1 != dir2`

#### Scenario: Hostname 返回非空字符串

- **WHEN** z42 用户在 CI 上调 `OperatingSystem.Hostname()`
- **THEN** 返回非空字符串
- **AND** wasm target 上返回 `""`（graceful degrade）

#### Scenario: CpuCount 至少为 1

- **WHEN** z42 用户调 `OperatingSystem.CpuCount()`
- **THEN** 返回值 `>= 1`
- **AND** 与 Rust `std::thread::available_parallelism()` 一致（取 logical CPU 数）

#### Scenario: OsVersion 返回 OS 版本字符串

- **WHEN** z42 用户调 `OperatingSystem.OsVersion()`
- **THEN** 在 unix 上返回 `uname` 输出格式（如 `"Darwin 23.3.0 ..."`）
- **AND** 在 windows 上返回 `<Major>.<Minor>.<Build>` 格式
- **AND** 拿不到时返回 `""`（不抛异常）

### Requirement: Std.IO.Environment 扩展

#### Scenario: UnsetEnvironmentVariable 移除已存在的 key

- **WHEN** z42 用户先 `Environment.SetEnvironmentVariable("Z42_TEST_X", "v")`
- **AND** 再 `Environment.UnsetEnvironmentVariable("Z42_TEST_X")`
- **AND** 再 `Environment.GetEnvironmentVariable("Z42_TEST_X")`
- **THEN** 返回 `null`

#### Scenario: UnsetEnvironmentVariable 对不存在的 key 静默

- **WHEN** z42 用户调 `Environment.UnsetEnvironmentVariable("Z42_DEFINITELY_NOT_SET_XYZZY")`
- **THEN** 不抛异常
- **AND** 后续调用同样无效果

#### Scenario: GetEnvironmentVariables 返回 KEY=VALUE 平铺数组

- **WHEN** z42 用户先 `Environment.SetEnvironmentVariable("Z42_FLAG", "ok")`
- **AND** 调 `Environment.GetEnvironmentVariables()`
- **THEN** 返回 `string[]`
- **AND** 数组中**有**一项 `== "Z42_FLAG=ok"`
- **AND** 每一项至少包含一个 `=`（key / value 分隔符）

### Requirement: OS / Arch Kind 整数值稳定

#### Scenario: 整数值在 z42 / Rust 两侧严格一致

- **WHEN** Rust corelib `__platform_os_kind` 返回 `2` for macOS
- **THEN** z42 `OSKind.MacOS` 必须 == `2`
- **AND** z42 单测在每个 CI OS 上 assert `Platform.OSKindValue() == OSKind.<expected>`，
  保证未来漂移立刻发现

## MODIFIED Requirements

无（纯新增 stdlib API 和 corelib builtins）。

## IR Mapping

无新 IR 指令。`Std.Platform` / `Std.OperatingSystem` 通过 Tier 1 `[Native("__name")]`
builtin dispatch 路径，沿用 `Builtin` IR 指令。

## Pipeline Steps

- [ ] Lexer — 不影响
- [ ] Parser / AST — 不影响
- [ ] TypeChecker — 不影响（标准 stdlib class 发现）
- [ ] IR Codegen — 不影响
- [ ] VM interp — 不影响
- [x] **stdlib（z42.io）** — 新增 2 个 .z42 + 扩展 Environment.z42
- [x] **VM corelib** — 新增 `platform.rs` + `system.rs` + `fs.rs` 中追加 2 个 builtins
