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

### `Z42_CRASH_DIR` —— 内部 panic 抓盘

VM 内部 panic（`unwrap` / index OOB / `debug_assert!` failure 等）默认打印 backtrace
到 stderr 后 abort。设了 `Z42_CRASH_DIR` 后**额外**把报告落到 `<dir>/z42vm-crash-<ts_ns>.txt`：

```bash
mkdir -p /var/log/z42
Z42_CRASH_DIR=/var/log/z42 RUST_BACKTRACE=1 ./z42vm script.zbc

# 崩溃时：
# [panic hook] crash report written to /var/log/z42/z42vm-crash-1716659200000000000.txt
```

报告内容：z42vm 版本 / 平台 / build profile / panic 位置 / payload / Rust backtrace
（`RUST_BACKTRACE=1` 控制详细程度）。

> **Phase 2 待补**：OS signal handler（SIGSEGV / SIGABRT）+ z42 call stack 捕获
> 在独立 spec 落地后纳入同一报告（见 `docs/review.md` Part 4 D4 Phase 2）。

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
