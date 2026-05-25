# Design: OS signal handler

## Architecture

```
                    main()
                      │
                      ├─ Cli::parse()
                      ├─ init_tracing(verbose)
                      ├─ install_panic_hook()       ← Phase 1 (12cf7ef8)
                      ├─ install_signal_handlers()  ← Phase 2 (this spec)
                      │     │
                      │     ├─ open crash_report fd from Z42_CRASH_DIR (optional)
                      │     └─ register handler for each of 5 signals via
                      │         signal_hook_registry::register_signal_unchecked
                      │
                      ├─ load module / run script
                      └─ on signal X fires:
                            ↓
                  signal_handler::handle(X, info, _ctx)
                            │
                            │  async-signal context — strict rules
                            │
                            ├─ write_str(STDERR, "[z42vm signal SIGSEGV at ip=0x")
                            ├─ write_hex(STDERR, info.si_addr)
                            ├─ write_str(STDERR, "]\n")
                            ├─ write_banner(STDERR)             ← const banner
                            ├─ try_lock VmCore.vm_contexts
                            │     ├─ ok    → walk + write each VmContext.call_stack
                            │     └─ fail  → write "<call stack: lock contended>"
                            ├─ (if fd) write same content to crash_report fd
                            ├─ signal(X, SIG_DFL)
                            └─ raise(X)   ← kernel default takes over
```

## Decisions

### Decision 1: Crate selection — `signal-hook-registry` + `libc`

**问题**：z42 选信号库。

**选项**：
- A. **`signal-hook`** ── 高级 API，自带 monitor thread + channel
- B. **`signal-hook-registry`** ── 低级 `register_signal_unchecked` 直接注册 handler
- C. **裸 `libc::sigaction`** ── 不引入 dep；自己拼 sigaction struct

**决定**：选 B。理由：
1. 选项 A 的 monitor thread 永远在跑（~1 thread 永久开销 + 上下文切换） — 不值
2. 选项 C 跨平台 sigaction layout 容易出 bug（macOS / Linux / 不同 libc 版本字段顺序不同），signal-hook-registry 已为我们 abstract
3. signal-hook-registry 的 `register_signal_unchecked` 明确允许 unsafe async-signal handler；正是我们要做的

### Decision 2: 不在 signal handler 用 `format!` / `eprintln!` / Mutex `lock()`

**问题**：async-signal-safe primitive 限制极严，主流 Rust 库函数都不行。

**决定**：写 `signal_handler::sigsafe` 子模块，内含：
- `fn write_str(fd: i32, s: &[u8])` — 直接 `libc::write(2)` 循环
- `fn write_hex_u64(fd, v: u64)` — 栈 [u8; 18] buffer 手写
- `fn write_dec_u32(fd, v: u32)` — 栈 [u8; 10] buffer 手写
- 整个模块禁用 `#[allow(format_args)]`；CI lint check（review only — 没有 lint 强制工具）

锁的部分：用 `Mutex::try_lock()`，失败就降级写 placeholder。绝不 `lock()` —— 否则信号在持锁线程触发会死锁。

### Decision 3: Crash report 文件 open 时机 + 路径

**问题**：handler 不能在 signal context 里 `open(2)`（POSIX 允许但跨平台保险起见避开）。

**决定**：
- install 时：根据 `Z42_CRASH_DIR + install_ts_ns` 构造 path，`File::create()` 一次性打开，文件名固定。
- 把 fd 存到 `static SIGNAL_CRASH_FD: AtomicI32 = AtomicI32::new(-1)`。`-1` 表示未配置。
- handler 读 fd（atomic load），若 ≥ 0 就 `libc::write(fd, ...)` 写一份相同内容。
- File 不在 install 时 close — 程序整个生命周期持有该 fd（leak OK，进程退出 OS 回收）。

文件名 = 第一次 install 时的时间戳。**同一个 process 多次 panic 共用同一文件**（不太可能多次发生；如果发生，多个 panic 报告 append 到同一文件，分隔由 `=== z42vm internal panic ===` 头部清楚区分）。

### Decision 4: handler 内的 stack walk —— 锁层级

**问题**：要拿 z42 call stack 必须穿透：
1. `VmCore.vm_contexts: Mutex<Vec<VmContextPtr>>` — 全 process 一锁
2. 每个 `VmContext.call_stack: Arc<Mutex<Vec<VmFrame>>>` — 每个 thread 一锁

**决定**：
```rust
unsafe {
    // 1. try 外层锁
    let guard = match VM_CORE_REGISTRY.try_lock() {
        Ok(g) => g,
        Err(_) => {
            sigsafe::write_str(STDERR, b"=== z42 call stack: unavailable (vm_contexts lock contended) ===\n");
            goto_reraise();
        }
    };
    sigsafe::write_str(STDERR, b"=== z42 call stack (");
    sigsafe::write_dec(STDERR, guard.len() as u32);
    sigsafe::write_str(STDERR, b" VmContext(s)) ===\n");

    for (i, vmctx_ptr) in guard.iter().enumerate() {
        // VmContextPtr is a raw pointer + tid
        let vmctx = &*vmctx_ptr.as_ptr();
        sigsafe::write_str(STDERR, b"tid=");
        sigsafe::write_dec(STDERR, vmctx.tid());

        // 2. try 内层锁
        match vmctx.call_stack.try_lock() {
            Ok(frames) => {
                sigsafe::write_str(STDERR, b" frames=");
                sigsafe::write_dec(STDERR, frames.len() as u32);
                sigsafe::write_str(STDERR, b"\n");
                for (j, frame) in frames.iter().enumerate() {
                    sigsafe::write_frame_line(STDERR, j, frame);
                }
            }
            Err(_) => sigsafe::write_str(STDERR, b": <call stack lock contended>\n"),
        }
    }
}
```

**SAFETY**：
- `VM_CORE_REGISTRY` 必须是 process-wide global，handler 才能找到 contexts。当前 `VmCore.vm_contexts` 是 per-VmCore；需要把 VmCore 自己也注册到一个全局 `VM_CORES: Mutex<Vec<Weak<VmCore>>>`。**这是本 spec 的额外架构改动**。
- handler 持 raw `*const VmContext`：因为 `VmContext: !Unpin` (vm_context.rs:302)，registry 中的指针寿命对齐 VmContext 寿命，安全。
- `frame.file / func_name` 是 `String` ── 写它需读 String 的 ptr+len。`String` 在 sigsafe 里**只读**不 alloc，写 raw bytes — OK。

### Decision 5: `VmCore` 注册到全局表

**问题**：handler 怎么找到 VmCore？当前 VmCore 没有全局 registry。

**决定**：新建 `static VM_CORES: Mutex<Vec<Weak<VmCore>>>`（在 `vm_context.rs`）。`VmCore::new()` 注册（Arc → Weak push）；不需要显式注销（Weak 自然失效）。

handler 用 `try_lock()` 拿 list，walk live cores（`upgrade()` → 走每个 core 的 vm_contexts）。

替代方案：用 `thread_local!` 的 `CURRENT_VM`（已有，[`native/exports.rs:37`](src/runtime/src/native/exports.rs#L37)）—— 但 `thread_local!` 在 signal handler 里**不安全**（thread-local 访问可能 alloc）。所以走全局 Mutex<Vec<Weak>>。

### Decision 6: 不抓 Rust backtrace

**问题**：是否在 signal handler 里也 capture Rust backtrace？

**决定**：不抓。`std::backtrace::Backtrace::capture()` 内部 alloc，async-signal 不安全。如果用户开了 `RUST_BACKTRACE=1` + 想看 Rust stack，可以让 kernel 的 coredump 留给 lldb 看 —— 比强行 alloc 在 signal context 安全。

### Decision 7: 不用 `sigaltstack`

**问题**：SIGSEGV 因 stack overflow 触发时，原栈已满 —— handler 在原栈跑会立刻再 fault。

**决定**：Phase 2 不做 alt-stack。理由：
1. stack overflow 是少数 fault 类型，主流 case 是访问坏指针
2. `sigaltstack` 跨平台 size / mmap 细节多
3. 即便 handler 自己 fault，进程仍会 abort —— 失败模式可接受
4. Phase 2.3 单独 spec 加 alt-stack

### Decision 8: Handler 末尾 — reset to SIG_DFL + raise

**问题**：handler 写完后怎么终止进程？

**选项**：
- A. `libc::_exit(128 + signum)` — 精确控制 exit code，但**禁用 coredump**
- B. `signal(sig, SIG_DFL); raise(sig)` — 让 kernel 走默认路径，**保留 coredump**

**决定**：选 B。生产环境 admin 可能开了 `ulimit -c unlimited`；coredump 是 z42 之外的诊断渠道，不剥夺。

## Implementation Notes

### `signal_handler.rs` 模块结构

```rust
// src/runtime/src/signal_handler.rs

#[cfg(unix)]
use std::sync::atomic::{AtomicI32, Ordering};

#[cfg(unix)]
static SIGNAL_CRASH_FD: AtomicI32 = AtomicI32::new(-1);

/// Install POSIX signal handlers for 5 fatal signals.
/// Idempotent — second call is no-op (atomic CAS guard).
#[cfg(unix)]
pub fn install() {
    static INSTALLED: AtomicI32 = AtomicI32::new(0);
    if INSTALLED.swap(1, Ordering::SeqCst) != 0 { return; }

    // 1. Open crash_report fd from Z42_CRASH_DIR (if set)
    if let Ok(dir) = std::env::var("Z42_CRASH_DIR") {
        let path = std::path::Path::new(&dir).join(format!("z42vm-crash-{}.txt", install_ts_ns()));
        match std::fs::OpenOptions::new().create(true).append(true).open(&path) {
            Ok(file) => {
                let fd = file.into_raw_fd();  // leak; OS reclaims at exit
                SIGNAL_CRASH_FD.store(fd, Ordering::SeqCst);
                tracing::debug!("signal crash report fd opened: {}", path.display());
            }
            Err(e) => tracing::warn!("Z42_CRASH_DIR {} not writable: {e}; signal reports go to stderr only", path.display()),
        }
    }

    // 2. Register 5 signal handlers
    for &sig in &[SIGSEGV, SIGABRT, SIGFPE, SIGILL, SIGBUS] {
        // SAFETY: handler is async-signal-safe (see fn handler doc)
        unsafe {
            signal_hook_registry::register_signal_unchecked(sig, move || handler(sig))
                .expect("signal_hook_registry registration failed");
        }
    }
    tracing::debug!("OS signal handlers installed for SIGSEGV/SIGABRT/SIGFPE/SIGILL/SIGBUS");
}

#[cfg(unix)]
extern "C" fn handler(sig: i32) {
    // STRICT: async-signal-safe code only. No alloc, no Mutex::lock, no eprintln.
    sigsafe::write_str(libc::STDERR_FILENO, b"\n[z42vm signal ");
    sigsafe::write_str(libc::STDERR_FILENO, signal_name(sig));
    sigsafe::write_str(libc::STDERR_FILENO, b"]\n");

    write_banner(libc::STDERR_FILENO);
    write_call_stacks(libc::STDERR_FILENO);

    let fd = SIGNAL_CRASH_FD.load(Ordering::SeqCst);
    if fd >= 0 {
        sigsafe::write_str(fd, b"\n[z42vm signal ");
        sigsafe::write_str(fd, signal_name(sig));
        sigsafe::write_str(fd, b"]\n");
        write_banner(fd);
        write_call_stacks(fd);
        sigsafe::write_str(libc::STDERR_FILENO, b"[panic hook] crash report appended to Z42_CRASH_DIR fd\n");
    }

    // Reset + reraise — kernel default takes over (coredump if enabled)
    unsafe {
        libc::signal(sig, libc::SIG_DFL);
        libc::raise(sig);
    }
}

// sigsafe submodule — only async-signal-safe primitives
#[cfg(unix)]
mod sigsafe {
    pub fn write_str(fd: i32, bytes: &[u8]) {
        // Loop until all bytes written (handle EINTR / partial writes)
        let mut remaining = bytes;
        while !remaining.is_empty() {
            let n = unsafe { libc::write(fd, remaining.as_ptr() as *const _, remaining.len()) };
            if n <= 0 { break; }  // give up on error
            remaining = &remaining[n as usize..];
        }
    }
    pub fn write_dec_u32(fd: i32, mut v: u32) {
        // Stack buffer, no alloc
        let mut buf = [0u8; 10];
        let mut i = 10;
        if v == 0 { write_str(fd, b"0"); return; }
        while v > 0 && i > 0 { i -= 1; buf[i] = b'0' + (v % 10) as u8; v /= 10; }
        write_str(fd, &buf[i..]);
    }
    pub fn write_hex_u64(fd: i32, v: u64) {
        let mut buf = [0u8; 18];
        buf[0] = b'0'; buf[1] = b'x';
        let mut i = 18;
        let mut n = v;
        if n == 0 { write_str(fd, b"0x0"); return; }
        while n > 0 && i > 2 { i -= 1; buf[i] = b"0123456789abcdef"[(n & 0xf) as usize]; n >>= 4; }
        write_str(fd, b"0x"); write_str(fd, &buf[i..]);
    }
}

#[cfg(unix)]
fn signal_name(sig: i32) -> &'static [u8] {
    match sig {
        libc::SIGSEGV => b"SIGSEGV",
        libc::SIGABRT => b"SIGABRT",
        libc::SIGFPE  => b"SIGFPE",
        libc::SIGILL  => b"SIGILL",
        libc::SIGBUS  => b"SIGBUS",
        _             => b"UNKNOWN",
    }
}
```

### `vm_context.rs` 改动 —— VM_CORES global registry

```rust
// At top of vm_context.rs (or new sibling module)
pub(crate) static VM_CORES: Mutex<Vec<Weak<VmCore>>> = Mutex::new(Vec::new());

impl VmCore {
    pub fn new(...) -> Arc<Self> {
        let core = Arc::new(VmCore { ... });
        if let Ok(mut g) = VM_CORES.lock() {
            // Gc stale Weak entries occasionally
            g.retain(|w| w.strong_count() > 0);
            g.push(Arc::downgrade(&core));
        }
        core
    }
}
```

handler 从 `VM_CORES.try_lock()` 拿 list，每个 `Weak.upgrade()` 拿到 `Arc<VmCore>` 后走 `core.vm_contexts.try_lock()` 拿 contexts。

### `main.rs` 改动

```rust
fn main() -> Result<()> {
    let cli = Cli::parse();
    init_tracing(cli.verbose);
    install_panic_hook();              // existing (Phase 1)
    #[cfg(unix)]
    z42::signal_handler::install();    // new (Phase 2)
    // ... rest unchanged
}
```

## Compatibility

- POSIX (macOS / Linux): full feature
- Windows: `signal_handler` module not compiled; install call gated; binary works as before (no signal capture)
- Existing Phase 1 panic hook: unchanged — Rust panic path still works identically
- `Z42_CRASH_DIR` semantics: backwards-compatible extension (panic hook 也 honor 该 fd 现在 — 但 Phase 1 panic hook 仍走自己的 ts_ns 文件名，**不复用**这个 fd。可以在 follow-up 把两者统一）

## Testing Strategy

### 单元测试 (`signal_handler_tests.rs`)

- `write_dec_u32`：0 / 1 / 9 / 10 / 99 / 100 / 999 / u32::MAX → 写入正确 byte 数 + 内容
- `write_hex_u64`：0 / 0xff / 0xdeadbeef / u64::MAX → 正确 hex
- `signal_name`：5 个已知 signal → 正确字符串；unknown → "UNKNOWN"
- `install` idempotent：连调 2 次只装一次（用 INSTALLED atomic）

### 集成测试 (`tests/signal_handler_e2e.rs`)

- 测试用 helper binary `examples/signal_crash_helper.rs`：raise 一个指定 signal
- 主测试：`Command::new("signal_crash_helper").args(["SIGSEGV"]).env("RUST_BACKTRACE", "0").output()`
- assert stderr contains `[z42vm signal SIGSEGV`
- assert stderr contains `=== z42 call stack`
- exit code = 128 + SIGSEGV signum (139 on most POSIX)
- 5 个 signal 一个一个跑
- 加 1 个 `Z42_CRASH_DIR=` test：assert file appears + content matches

### 端到端验证

- `./scripts/test-all.sh` 全绿
- 用 `lldb` 看 SIGSEGV coredump 仍可生成（手动验证）
- 手测：写一个故意 SIGSEGV 的 native module，调用，确认报告

## Deferred / Future Work

### Phase 2.1: Windows Vectored Exception Handler

跟 Linux/macOS 完全不同的 API；单独 spec。

### Phase 2.2: Stack-pointer-based 线程归因

当前只标 `tid=<n>`；不区分哪个 thread 真正 fault。完整归因需要：
- handler 读 `siginfo_t.si_addr` + `ucontext_t` 拿到 fault thread 的 stack pointer
- 在 VM_CORES walk 时找到 stack pointer 落在哪个 thread 的栈范围
- 把那个 thread 标 `[FAULTED]`，其他标 `[OTHER]`

### Phase 2.3: `sigaltstack`

为 SIGSEGV 准备专属信号栈，处理 stack overflow case。

### Phase 2.4: Rust backtrace in signal context

需要 `backtrace` crate 提供 async-signal-safe 路径（目前 `backtrace::Backtrace::new()` 用 allocator）。或者用 libunwind 直接走 ucontext。

### Phase 2.5: 统一 panic hook + signal handler crash 文件

Phase 1 panic hook 自己开文件用各自 ts_ns；Phase 2 signal handler 用 install 时的 ts_ns。后续 spec 把两者复用同一 fd。
