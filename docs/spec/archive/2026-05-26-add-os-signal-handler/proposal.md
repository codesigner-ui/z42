# Proposal: add OS signal handler for hard-crash z42 call stack capture

## Why

`12cf7ef8` 已落地 [Rust panic hook + `Z42_CRASH_DIR`](../../archive/2026-05-25-add-os-signal-handler/) (docs/review.md Part 4 D4 Phase 1)，覆盖 Rust 主动 `panic!` / `unwrap` / index OOB / `debug_assert!` failure：能 print VM version + panic 位置 + payload + Rust backtrace。

但**真正的硬崩溃**走 OS signal 通道，绕过 Rust panic 机制：

| 触发场景 | 信号 | 当前行为 |
|---|---|---|
| JIT 生成的代码访问坏指针 | SIGSEGV | 进程直接 abort，0 信息 |
| Native FFI 模块写坏内存 | SIGSEGV / SIGBUS | 同上 |
| 整数除零（i64 / interp 已捕获，但 native / JIT 路径可能漏） | SIGFPE | 同上 |
| 非法指令 / corrupt JIT code | SIGILL | 同上 |
| `libc::abort()` from native（断言 / OOM） | SIGABRT | 同上 |

生产环境 z42vm 硬崩溃 = 纯 OS coredump，**需要 lldb + symbols 才能看到 Rust stack，z42 call stack 完全丢失** —— 是 production-blocker。

### 触发症状

```bash
# JIT 出 bug 时
$ ./z42vm script.zbc
[1]    12345 segmentation fault  ./z42vm script.zbc
# ↑ 这是当前的全部诊断信息
```

期望：

```
[z42vm signal SIGSEGV at instruction pointer 0x10044a8c0]
z42vm 0.1.0 (release, macos/aarch64)
=== z42 call stack (thread tid=42, frames=3) ===
  #0  MyClass.DoStuff at /Users/.../user.z42:42:8
  #1  Main           at /Users/.../user.z42:5:5
  #2  <entry>
============================
[panic hook] crash report written to /var/log/z42/z42vm-crash-1716659200000000000.txt
[1]    12345 segmentation fault (core dumped)
```

### 不修会怎样

- 生产部署 z42vm 出 SIGSEGV → 无诊断，无 repro 路径
- JIT bug 报告极困难（用户只能给个 coredump）
- native FFI 调试体验差（不知道哪个 `extern "C"` 写坏了内存）
- Phase 1 panic hook 价值被严重削弱（覆盖率只有"主动 panic"一半）

## What Changes

### 主体：选项 B "controlled unsafe" capture

在 panic hook install 之后额外注册 5 个 OS signal handler：`SIGSEGV / SIGABRT / SIGFPE / SIGILL / SIGBUS`。

Handler 行为（async-signal context 严格守则）：

1. 写固定 header 到 stderr：`[z42vm signal <name> at ip=<hex>]`（用 `libc::write(2)` 直接 fd 写）
2. 写 build banner：版本 / target / profile（const 字符串）
3. **try_lock** `VmCore.vm_contexts` registry —— 拿不到就跳过 z42 stack 捕获 + 写 `<call stack: lock contended, unavailable>`
4. 拿到锁后遍历每个 VmContext 的 `call_stack: Mutex<Vec<VmFrame>>`：同样 try_lock
5. 每个 `VmFrame` 写一行：`#<idx>  <func_name> at <file>:<line>:<col>`（手写 itoa，不用 format!）
6. 如果 `crash_report_fd` 已 pre-open（install 时根据 `Z42_CRASH_DIR` 打开），同样的内容也写到该 fd
7. 重置 signal 到 `SIG_DFL` + `raise(sig)` —— 让 kernel 走默认行为（coredump if enabled，正确 exit code）

### Crate 选择

- **`signal-hook-registry`** —— 低级 handler 注册（不带额外 thread）
- **`libc`** —— `write(2)` / `raise(2)` / `signal(2)`

不引入 `signal-hook` 高级包（带 monitor thread + channel，开销永远在），不引入 `backtrace` 在 signal context（allocator 重入）。

### 配套清理

无 workaround 可清。Phase 1 panic hook 不动 —— 它仍然处理 Rust panic，本 spec 仅 **额外**加 OS signal 通道。两条通道**互不干扰**（Rust panic 不走 OS signal handler，OS signal 不触发 Rust panic hook）。

## Scope（允许改动的文件）

| 文件路径 | 变更 | 说明 |
|---|---|---|
| `src/runtime/Cargo.toml` | MODIFY | 加 `signal-hook-registry` + `libc` deps |
| `src/runtime/src/main.rs` | MODIFY | 在 `install_panic_hook()` 后调 `install_signal_handlers()`；新增该函数 ~150 LOC |
| `src/runtime/src/signal_handler.rs` | NEW | async-signal-safe handler 实现（独立模块）+ 5 个 signal 注册 + crash_report fd 管理 + fmt-free 写数字 helper |
| `src/runtime/src/lib.rs` | MODIFY | `pub mod signal_handler;` (gated `#[cfg(unix)]`) |
| `src/runtime/src/signal_handler_tests.rs` | NEW | 单元测试：fmt-free itoa / 信号常量映射 / Header 格式化（**不**真的发信号 — 那是 e2e 测试） |
| `src/runtime/tests/signal_handler_e2e.rs` | NEW | 集成测试：fork 子进程 raise SIGSEGV / SIGABRT，parent 读 stderr + 校验 marker + Z42_CRASH_DIR 文件出现 |
| `src/runtime/src/main.rs` | MODIFY | 之前的 panic_hook.rs 注释更新 "Phase 2 待补 → Phase 2 已落地" |
| `docs/workflow/debugging.md` | MODIFY | 更新 ops 钩子段：`Z42_CRASH_DIR` 现在也接 OS signal，列 5 个被捕获的 signal |
| `docs/review.md` | MODIFY | Part 4 D4 状态 P0 → ✅ |
| `docs/roadmap.md` | (optional) | 不动 — 这是 ops 改进不动 roadmap pipeline |

**只读引用**：
- `src/runtime/src/vm_context.rs` — 看 `VmCore.vm_contexts` + `VmContext.call_stack` 结构
- `src/runtime/src/exception/mod.rs` — 看 `VmFrame` 字段

## Out of Scope

- **Windows VEH (Vectored Exception Handler)** —— 完全不同的 API；Phase 2.1 后续 spec
- **z42 stack trace 完整化（含 module / IR offset）** —— 当前只用 `VmFrame.{func_name, file, line, column}`，已经够诊断；spec 不去丰富 `VmFrame` 本身
- **Rust backtrace 在 signal context 捕获** —— allocator 重入风险，依赖 `backtrace` crate 内部状态，不做
- **Stack-pointer-based 线程识别** —— 只标 "tid=<n>"，不去算"哪个 thread fault"。多 thread crash 的精确归因 Phase 2.2 后续
- **Coredump 抑制 / 自定义 dump 格式** —— kernel 默认行为不动
- **SIGINT / SIGTERM graceful shutdown** —— 不同语义（用户主动）；后续单独 spec（不算 crash）
- **`backtrace-on-stack-overflow` 风格 alt-stack** —— 用 `sigaltstack(2)` 给 SIGSEGV 自己的栈以处理 stack overflow case。复杂；Phase 2.3
- **POSIX `psiginfo` / `siginfo_t` 详细解析** —— 只用 `signum`，不解析 `si_addr` / `si_code`（虽然可以加但复杂度成本不值；future enhancement）

## Open Questions

1. **`crash_report_fd` 多 process 复用怎么避免文件名冲突？**
   - 当前 Phase 1 用 unix_ts_ns；OS signal 同样可用
   - 但 install 时**预开**fd 意味着同一 process 多次 panic 共用同一文件 — 也许更 OK（一个 process 多次 crash 罕见）
   - **建议**：install 时根据 `Z42_CRASH_DIR + ts_ns` 创建一个 fd；后续多次 panic 都 append 到该 fd。文件名只反映"第一次 panic 时刻"
2. **handler 是否 chain 到 default？**
   - 当前选项 B 写完信息后重置到 `SIG_DFL` 再 `raise()` 让 default 处理（coredump）
   - 替代方案：直接 `_exit(128 + signum)` —— 精确控制 exit code，但不出 coredump
   - **建议**：reset + raise，保留 coredump 能力（生产环境 admin 可能开了 `ulimit -c unlimited`）
3. **集成测试用 fork or std::process::Command?**
   - fork 在测试代码里需要 `unsafe`；Command 启动 helper binary 干净但要 build 一个测试-only binary
   - **建议**：测试-only `examples/signal_crash_helper.rs` + `std::process::Command`，主进程 spawn 它然后 read stderr + check exit code

User 裁决：上述 3 个 建议默认采纳，如有不同意见在 Gate 6.5 提出。
