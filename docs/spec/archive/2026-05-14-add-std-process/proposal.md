# Proposal: Std.Process — cross-platform command execution

## Why

仓库里 13 个 `scripts/*.sh`（~1800 行）需要往 z42 迁移以统一跨平台调用入口
（见 `docs/workflow/building/wasm.md` 和未来 Windows 支持）。90% 的 shell
脚本内容是 **invoke 外部工具 + 检查 exit code + 捕获输出**，z42 stdlib
当前完全没有 process API，是阻塞迁移的 P0 缺口。

跨平台命令执行的设计空间已经收敛：.NET `System.Diagnostics.Process`、
Rust `std::process::Command`、Python `subprocess`、Go `exec.Cmd` 不约而同采用
**argv-style + 不走 shell + stdio 三态枚举** 的形态。本 spec 把这套约定
原样搬进 z42 stdlib，让"跨平台一致性"在 API boundary 就被强制：

- **argv 数组进出**：禁止 `Process.Run("cargo build --release")` 单字符串
  形态，从根上消除 Windows ↔ Unix quoting 不一致
- **默认不走 shell**：globbing / pipe / 变量展开等"shell features"由 z42
  原生等价物（`Directory.EnumerateFiles` / `.Stdin(Pipe)` 拼装 / `Environment.Get`）
  承担，跨平台行为定义明确
- **stdio 用 `Stdio.{Inherit, Pipe, Null, File}` 枚举**：替代散落的 bool 位

**不做会怎样**：脚本只能继续靠 sh / .ps1 双发车维护，Windows 支持永远是
"看脚本怎么写的"二等公民；后续的 `z42 run scripts/test-all` 统一调用入口
彻底起不来。

## What Changes

### 新增 `Std.Process` stdlib class（z42.io）

argv-style builder API：

```z42
var result = new Process("cargo")
    .Arg("build").Arg("--release")
    .WorkingDirectory("src/runtime")
    .Env("RUSTFLAGS", "-C opt-level=3")
    .Stdin(Stdio.Null)
    .Stdout(Stdio.Pipe)
    .Stderr(Stdio.Inherit)
    .Run();           // 阻塞，返回 ProcessResult

result.ExitCode;       // int
result.Stdout;         // string (UTF-8 解码)
result.Stderr;         // string
result.EnsureSuccess(); // exit != 0 抛 ProcessExitException
```

两个入口：
- `Run()` — 阻塞 + 默认 capture（stdout/stderr 默认 `Pipe`），脚本场景
- `Spawn()` — 返回 `ProcessHandle`，stdio 默认 `Inherit`，wrapper 透传场景

### 新增 `Stdio` 枚举（z42.io 同包）

```z42
public enum Stdio {
    Inherit,           // 子进程继承父 stdio
    Pipe,              // 父捕获
    Null,              // 黑洞
    File(string path), // 重定向到文件
}
```

第一个 spec 不引入 ADT 关联值（`File(path)` 用专门的 `Stdio.ToFile(path)`
静态构造方法替代，参见 design.md 决定）。

### 新增 VM corelib builtins（L1 dispatch）

z42.io 用 L1 `[Native("__name")]` builtin dispatch（参见 `File.z42` /
`Console.z42` 现状），**不引入独立 Rust crate**。直接在
`src/runtime/src/corelib/process.rs` 新增 builtins 包装
`std::process::Command`，注册到 `corelib/mod.rs` 的 dispatch_table。
Windows / Linux / macOS 差异由 `std::process` 承担。

### 新增异常类型

- `Std.ProcessStartException` —— 可执行文件找不到 / 权限拒绝 / fork 失败
- `Std.ProcessExitException` —— `EnsureSuccess()` 发现 exit != 0 时抛出，
  Message 含 ExitCode + 截断的 stderr

### 不做的部分（明确划界）

- ❌ **Shell 调用**：不提供 `Process.RunShell(string)` 等价物。需要 globbing
  用 `Directory.EnumerateFiles`，需要 pipe 用 builder 拼装 `Spawn` + stdio
  转发。若实际迁移期发现高频痛点，单独 spec 再补。
- ❌ **PowerShell / cmd.exe 检测启动器**：z42 不内置任何"猜 shell"逻辑；
  用户要跑 PowerShell 就显式 `new Process("pwsh").Arg("-c").Arg(...)`。
- ❌ **后台 daemon / 长期进程管理**：本 spec 只覆盖"启动 → 等待结束 → 拿结果"
  的脚本场景，pidfile / signal 转发 / 进程组等留给后续。
- ❌ **Async / 异步 API**：跟 wasm v0.1 同步 invoke 一致，本 spec 全部同步。
  长任务 z42 用户自己用 `Spawn` 拿 handle 后非阻塞 `TryWait`，async 等
  L3-A1 落地后另开 spec。
- ❌ **Pipe / shell-like 操作符**：不引入 `Process.A.PipeTo(B)` 这种链式糖。
  用户用 `Spawn(stdout=Pipe)` + 手动接管。

## Scope（允许改动的文件）

| 文件路径 | 变更 | 说明 |
|---|---|---|
| `src/libraries/z42.io/src/Process.z42` | NEW | `Std.Process` builder + `ProcessResult` + `ProcessHandle` 类定义 |
| `src/libraries/z42.io/src/Stdio.z42` | NEW | `Std.Stdio` enum + `Stdio.ToFile(string)` 静态构造 |
| `src/libraries/z42.io/src/Exceptions/ProcessStartException.z42` | NEW | 进程启动失败异常 |
| `src/libraries/z42.io/src/Exceptions/ProcessExitException.z42` | NEW | EnsureSuccess 失败异常 |
| `src/libraries/z42.io/src/Process.z42` | NEW | `Std.Process` builder（externs：`__process_run` / `__process_spawn` / `__process_wait` 等）|
| `src/libraries/z42.io/src/ProcessHandle.z42` | NEW | Spawn 返回的 handle 类（Wait / TryWait / Kill / WriteStdin / CloseStdin）|
| `src/libraries/z42.io/src/ProcessResult.z42` | NEW | Run / Wait 返回的结果类（ExitCode / Stdout / Stderr / EnsureSuccess）|
| `src/runtime/src/corelib/process.rs` | NEW | Rust builtins 实现，包装 `std::process::Command` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | dispatch_table 注册新 `__process_*` builtins |
| `src/runtime/src/corelib/process_tests.rs` | NEW | Rust 单测：argv passing / env / stdio / timeout / exit code |
| `src/libraries/z42.io/tests/process_basic.z42` | NEW | Run + ExitCode + Stdout 捕获 |
| `src/libraries/z42.io/tests/process_failure.z42` | NEW | 找不到 exe / exit != 0 + EnsureSuccess |
| `src/libraries/z42.io/tests/process_env_cwd.z42` | NEW | Env + WorkingDirectory 生效 |
| `src/libraries/z42.io/tests/process_stdio.z42` | NEW | Pipe / Inherit / Null / ToFile 四种 stdio |
| `examples/process/hello.z42` | NEW | 一个端到端示范，复用到 doc |
| `docs/design/runtime/stdlib-process.md` | NEW | 实现原理：argv 路径、stdio handling、跨平台 quirks |
| `docs/design/language/language-overview.md` | MODIFY | 同步"Std.Process 是脚本工具的官方 API"一句 |
| `docs/workflow/building/wasm.md` 或类似 | — | 不动；本 spec 不改 build 流程 |

**只读引用**：

- `src/libraries/z42.io/src/{File,Path,Environment}.z42` — 同包 `[Native("__name")]`
  风格对齐
- `src/runtime/src/corelib/{fs,io}.rs` — 现成 builtins 实现模式（Value 解包 /
  错误转换 / register 写法）
- `src/libraries/z42.time/src/TimeSpan.z42` — `.Timeout()` 参数类型

## Out of Scope

- 后续把 `scripts/*.sh` 改写成 `scripts/*.z42`（独立 spec / batch 工作）
- Glob / `Directory.EnumerateFiles` / 其他 z42.io 扩展（独立 spec）
- CLI 参数解析器 `Std.CommandLine`（独立 spec）
- `z42 run scripts/foo.z42` 统一调用入口（独立 spec，依赖本 spec + glob）
- Async API（依赖 L3-A1）
- Bootstrap 脚本（`build-stdlib.sh` / `package.sh`）的迁移问题

## Decisions（User 确认 2026-05-13）

1. **enum 关联值**：z42 当前 enum 只是命名整数常量（L1/L2 阶段不引入 ADT，
   见 [CLAUDE.md]）。`Stdio` 实现为 **sealed class + 公开静态实例 +
   `ToFile(path)` 静态工厂**：
   ```z42
   public sealed class Stdio {
       public static readonly Stdio Inherit;
       public static readonly Stdio Pipe;
       public static readonly Stdio Null;
       public static Stdio ToFile(string path);
   }
   ```
2. **可执行文件相对路径**：`new Process("./build.sh")` 中的 `./` **相对 cwd**
   解析（Rust `std::process::Command` 默认行为，符合 shell 直觉）。
   `.WorkingDirectory(d)` 只影响**子进程**的初始 cwd，不影响 `argv[0]` 解析。
3. **环境变量默认值**：默认**全继承**父进程 env（脚本场景必需 `PATH`）。
   `.Env(k, v)` 增删改单条；`.ClearEnv()` 清空；`.EnvFromMap(m)` 完全替换。
4. **Timeout 进本 spec**：`.Timeout(timespan)` builder 项，类型为
   `Std.Time.TimeSpan`（z42.time 已落地，`TimeSpan.FromSeconds(30)` 等）。
   超时后自动 `KillForce` 子进程并抛 `ProcessTimeoutException`。
5. **stdin 数据写入**：**两种都支持**。
   - 糖：`.StdinBytes(byte[])` 或 `.StdinString(string)` builder 项，
     `Run()` 时一次性 write 后 EOF。
   - 完整：`Spawn()` 后通过 `handle.WriteStdin(byte[])` / `CloseStdin()`
     流式写。
6. **stdout / stderr 类型**：默认 `ProcessResult.Stdout: string`（UTF-8 解码，
   错误字节用 U+FFFD 替换），额外暴露 `StdoutBytes: byte[]`。与
   `File.ReadAllText` / `ReadAllBytes` 同模式。
