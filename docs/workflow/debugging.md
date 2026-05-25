# 调试工作流

> **状态**：📋 部分可用。完整 DAP 集成在 SemVer **0.8.7** 启用。

## VM 运行时 ops 钩子（2026-05-25 起）

z42vm 内置 3 个 ops/devex 设施，无需重编译 / 重启即可控制日志、查看版本、抓崩溃。

### `--info` —— 打印构建信息

```bash
$ z42vm --info
z42vm 0.1.0
target: macos
arch: aarch64
build profile: release
features: jit, native-interop
exec modes: interp, jit
Z42_LIBS: (unset; falls back to artifacts/build/libs/release)
Z42_PATH: (unset)
Z42_LOG: (unset; defaults to z42=warn / z42=info under --verbose)
libs dir: /Users/.../artifacts/build/libs/release
```

bug 报告 + CI 预检 / 跨机器对比 build profile 用。

### `Z42_LOG` —— per-module 日志过滤

`tracing-subscriber` directive 语法 — 用 `=` 控制 level，逗号分隔多条规则。
默认 `z42=warn`；`--verbose` 是 `z42=info`；`Z42_LOG` 覆盖前两者。

```bash
# 看 JIT 编译细节 + GC 全部 trace，其他模块只看 warn 以上
Z42_LOG=z42::jit=debug,z42::gc=trace,z42=warn ./z42vm script.zbc

# 单一全局 level
Z42_LOG=z42=debug ./z42vm script.zbc

# 只看 warn 以上（即便 --verbose 也安静）
Z42_LOG=z42=warn ./z42vm --verbose script.zbc
```

可用 target：`z42` / `z42::interp` / `z42::jit` / `z42::gc` / `z42::native::*` /
`z42::metadata::*` 等（每个 mod 自己的路径）。完整 directive 语法见
<https://docs.rs/tracing-subscriber/latest/tracing_subscriber/filter/struct.EnvFilter.html>。

### `Z42_CRASH_DIR` —— 内部 panic + OS signal 抓盘

VM 内部 panic（`unwrap` / index OOB / `debug_assert!` failure 等）**和** OS 信号
（SIGSEGV / SIGABRT / SIGFPE / SIGILL / SIGBUS）默认都打印诊断到 stderr 后 abort。
设了 `Z42_CRASH_DIR` 后**额外**把报告落到 `<dir>/z42vm-crash-<ts_ns>.txt`：

```bash
mkdir -p /var/log/z42
Z42_CRASH_DIR=/var/log/z42 RUST_BACKTRACE=1 ./z42vm script.zbc

# Rust panic 崩溃：
# [panic hook] crash report written to /var/log/z42/z42vm-crash-1716659200000000000.txt

# OS 信号崩溃（JIT 出 bug / native FFI 写坏内存等）：
# [signal hook] crash report appended to Z42_CRASH_DIR fd
```

**Rust panic 报告内容**（Phase 1, 12cf7ef8 / 2026-05-25）：
z42vm 版本 / 平台 / build profile / panic 位置 / payload / Rust backtrace
（`RUST_BACKTRACE=1` 控制详细程度）。

**OS signal 报告内容**（Phase 2, add-os-signal-handler / 2026-05-26）：
信号名 / build banner / **所有 thread 的 z42 call stack**（一行一帧：
`#<idx>  <func_name> at <file>:<line>:<col>`）。报告写完后 `signal()` 重置到
`SIG_DFL` + `raise()`，让 kernel 走默认 abort + coredump（`ulimit -c unlimited` 仍生效）。

捕获的 5 个信号：

| 信号 | 典型触发 |
|---|---|
| **SIGSEGV** | 访问坏指针 — JIT bug、native FFI 写坏内存 |
| **SIGABRT** | `libc::abort()` 调用 — native module 内部断言失败 / glibc OOM |
| **SIGFPE** | 整数除零 — z42 interp 已挡，native / JIT 路径漏网 case |
| **SIGILL** | 非法指令 — corrupt JIT code |
| **SIGBUS** | 对齐错误 / mmap 越界 — 偶发 ARM64 |

**Lock contended 降级**：报告依赖 `try_lock` 拿 VM_CORES 注册表 + 每个 thread 的
`call_stack`。如果信号触发时另一线程持锁（GC mark 阶段等），handler 写
`<call stack lock contended>` placeholder，**不死锁，不丢报告**，进程仍正常 abort。

**Windows 暂未支持**：现仅 POSIX (macOS / Linux)。Windows VEH (Vectored Exception
Handler) 是 Phase 2.1 后续 spec — Windows build 编译过但无信号捕获。

## 当前可用

### 编译器（C#）

VS / VS Code / Rider 设 breakpoint 后：

```bash
dotnet run --project src/compiler/z42.Driver -- <args>
```

直接附加 debugger 即可。

### VM（Rust）

```bash
# lldb（macOS / Linux）
lldb -- ./artifacts/build/runtime/debug/z42vm <file.zbc>

# gdb
gdb --args ./artifacts/build/runtime/debug/z42vm <file.zbc>

# rust-analyzer + VS Code launch.json：照常 cargo run / cargo test
```

### z42 用户代码

z42c 默认产出 debug-symbol 信息：

- **`.zbc`（debug build）**：DBUG section 内嵌 `LineTable` + `LocalVarTable`
- **`.zbc`（release build）+ `.zsym` sidecar**：split-debug-symbols；运行时按 BLAKE3-128 build_id 自动配对加载

VM trace 已自动显示 `<file>:<line>:<col>` + 局部变量名（DBUG section 加载到时）。详见 [`docs/design/runtime/vm-architecture.md`](../design/runtime/vm-architecture.md) "Sidecar 调试符号加载" 段。

```bash
# 产出 sidecar
( cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release )   # release 默认 strip + 产出 .zsym
```

## 0.8.7 之后（DAP debugger）

`z42-dap` 适配层启用后支持：

- VS Code / JetBrains 经 DAP 协议调试 z42 源码
- Step / breakpoint / locals / watch
- JIT / AOT 模式下的 step（详见 Q14 待裁决）

详见 [`docs/roadmap.md`](../roadmap.md) §0.8.x charter。

## 当前 dump 工具

```bash
# zbc 反汇编为 zasm 文本
dotnet run --project src/compiler/z42.Driver -- disasm <file.zbc>

# AST dump（C# 端）
dotnet run --project src/compiler/z42.Driver -- <file.z42> --dump-ast

# Build ID
dotnet run --project src/compiler/z42.Driver -- build-id <file.zbc | file.zpkg | file.zsym>
```
