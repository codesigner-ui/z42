# Platform Abstraction Layer (PAL)

> review.md Part 1 P2 — single home for every `#[cfg(target_os)]` /
> `#[cfg(unix)]` / `#[cfg(windows)]` split in the runtime. Phase 1
> (2026-06-03, add-pal-system-phase1) shipped the module scaffold + first
> migrated concern (`system`); this document codifies the long-form design
> + Phase 2-N migration plan.

## 设计目标

让 runtime 其余模块**零 cfg 调用** OS 服务。OS-specific 实现集中在
`src/runtime/src/pal/<concern>.rs`，公开 surface 返回 OS-neutral 类型
(`Option<String>` / `Result<T, E>`)。

## CoreCLR 对照

CoreCLR `src/coreclr/pal/` 含 ~50 个文件抽象：

| Concern | CoreCLR pal/ 文件 | z42 pal/ 计划 | Status |
|---|---|---|---|
| Thread / mutex / TLS | `thread.cpp` / `mutex.cpp` | `pal/thread.rs` (Phase 4) | Pending |
| File I/O | `file.cpp` / `path.cpp` | `pal/fs.rs` (Phase 2) | Pending |
| Signal / exceptions | `signal.cpp` | `pal/signal.rs` (Phase 3) | Pending |
| Memory / mmap | `virtual.cpp` | `pal/mem.rs` (Phase 5) | Pending |
| Process / environ | `process.cpp` / `environ.cpp` | `pal/system.rs` 已起步 | Phase 1 done |
| Clock / time | `time.cpp` | `pal/clock.rs` (post-MVP) | Pending |

z42 不抄 CoreCLR 的 PAL 内部分层（CoreCLR 还有 `pal/src/<arch>/` per-arch
ASM）—— Cranelift / Rust 已经兜底 arch 差异。z42 PAL 只关心 **OS 差异**。

## z42 Phase 1 现状（2026-06-03）

`src/runtime/src/pal/`:

```
pal/
├── mod.rs               — 模块入口 + 子模块 re-exports
├── README.md            — 快速 orientation
├── system.rs            — Phase 1：hostname / os_version
└── system_tests.rs      — 单元测试
```

`pal::system` Phase 1 surface：

```rust
pub fn hostname() -> Option<String>;
pub fn os_version() -> String;
```

`#[cfg(unix)]` 走 libc::gethostname / libc::uname；`#[cfg(target_arch =
"wasm32")]` 返回 None / "wasm"；其他 (Windows-without-impl) 返回 None / 空
字符串。**所有 cfg 切分都在 `pal/system.rs` 内部** —— `corelib/system.rs`
零 cfg。

## 不变量（必须遵守）

1. **OS-neutral surface**：pub fn signatures 不带 OS 类型（不暴露
   `libc::c_char` / `winapi::HANDLE` 等）
2. **每个 concern 一个文件**：`#[cfg(...)]` 切分在文件内部，consumer 零 cfg
3. **graceful degrade**：未实现的平台返回 `None` / `""` / `Err` 而非 panic
4. **错误 OS-neutral**：`Option<T>` / `Result<T, E>` 抽象，不暴露 errno
5. **每个 pal 子模块必须有 unit tests**：smoke test 覆盖 unix 路径 + non-unix
   sentinel return

## Phase 2-N Migration Plan

### Phase 2: `pal/fs.rs` — 文件系统

迁移 `corelib/fs.rs` 中 4 处 `#[cfg(unix)]` 块：

```rust
// pal/fs.rs (Phase 2)
pub fn make_executable(path: &str) -> Result<()>;
pub fn symlink(src: &str, dst: &str) -> Result<()>;
pub fn read_permissions(path: &str) -> Result<u32>;
pub fn set_permissions(path: &str, mode: u32) -> Result<()>;
```

`corelib/fs.rs::builtin_file_make_executable` 等改 call `pal::fs::*`，
零 cfg。

### Phase 3: `pal/signal.rs` — POSIX signal handler

`signal_handler.rs` 整文件 `#![cfg(unix)]` —— 迁移：

```rust
// pal/signal.rs (Phase 3)
pub fn install_handler(sig: Signal, handler: SignalHandler);
pub fn signal_safe_write_str(fd: SignalSafeFd, s: &str);
```

Windows VEH（Phase 3.1）走相同接口的不同 impl。

### Phase 4: `pal/thread.rs` — 多线程基础

当前 `thread/` 是 stub。Phase 4 引入 PAL 抽象 thread spawn / TLS /
join 作为 multi-thread runtime 的底座（review.md add-multithreading-foundation
spec 进行中，可对接）。

```rust
// pal/thread.rs (Phase 4)
pub fn spawn<F>(f: F) -> ThreadHandle where F: FnOnce() + Send;
pub fn current_id() -> ThreadId;
pub fn yield_now();
```

### Phase 5: `pal/mem.rs` — 页对齐分配 / mmap

GC bump allocator（review.md C6）需要页对齐大块虚拟内存。先用 std::alloc
顶住，Phase 5 切到 mmap / VirtualAlloc 抽象。

```rust
// pal/mem.rs (Phase 5)
pub fn alloc_pages(n: usize) -> PageBlock;
pub fn protect(block: &PageBlock, prot: Protection);
pub fn free_pages(block: PageBlock);
```

## Platform feature gates

Cargo.toml 的 `wasm` / `ios` / `android` preset feature 与 PAL 互补：

| Feature | PAL 行为 |
|---|---|
| 默认（无 preset） | 完整 unix / windows 实现 |
| `wasm` | 所有 PAL fn 走 `cfg(target_arch = "wasm32")` 分支 |
| `ios` / `android` | 走 unix 分支（mobile 仍是 POSIX）；个别 syscall 受 sandbox 约束 |

PAL **不** 是 Cargo feature；它是 cfg-based static dispatch。Feature gate
的角色是关掉某些 module（如 `jit`），PAL 始终在。

## Deferred / Future Work

- **Windows real impl** — Phase 1-N 各 concern 的 Windows 分支都 stub。等
  Windows CI runner 投产后实现（review.md backlog 项）。
- **wasm32-wasi** — 目前 wasm32 全 stub，wasi 有 fs / network syscall 可
  实装；视用户需求引入。
- **PAL trait 化** — 目前 PAL 是 free functions + cfg dispatch。若未来需要
  runtime-injectable PAL（测试 mock / 仿真平台），可加 `trait Pal` + Vtable，
  保持 cfg-based 实现是默认。
