# pal — Platform Abstraction Layer

## 职责

集中处理 `#[cfg(target_os)]` / `#[cfg(unix)]` / `#[cfg(windows)]` 分支，让
runtime 其余模块**零 cfg 调用** OS 服务。每个 concern 一个文件，公开 surface
返回 OS-neutral 类型。

**当前状态（review.md Part 1 P2 Phase 1, 2026-06-03）**：刚起步。完整设计 +
Phase 2-N migration 路径见 [`docs/design/runtime/pal.md`](../../../../docs/design/runtime/pal.md)。

## 核心文件

| 文件 | 职责 |
|------|------|
| `system.rs` | `hostname()` / `os_version()` —— Phase 1 |

未来：`fs.rs` (Phase 2) / `signal.rs` (Phase 3) / `thread.rs` (Phase 4) /
`mem.rs` (Phase 5)。

## 入口点

`pal::system::hostname()` — `Option<String>`，None 在 Windows / WASM
`pal::system::os_version()` — `String`，空字符串表示 syscall 失败

## 不变量（必须遵守）

1. **OS-neutral surface**：pub fn signatures 不带 OS 类型（不 `libc::c_char` /
   `winapi::HANDLE` 等）
2. **每个 concern 一个文件**：`#[cfg(...)]` 切分在内部，consumer 零 cfg
3. **graceful degrade**：未实现的平台返回 None / 空字符串而非 panic
4. **错误也 OS-neutral**：用 `Option<T>` / `Result<T, E>` 抽象，不暴露 errno

## 依赖关系

依赖 `libc` (unix-only)。其他 cargo feature 见
`docs/design/runtime/pal.md` "platform feature gates" 节。
