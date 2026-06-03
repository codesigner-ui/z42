# Tasks: PAL Phase 1 — module + system concern + design doc

> 状态：🟢 已完成 | 创建：2026-06-03 | 完成：2026-06-03 | 类型：refactor + 加法（建 PAL 抽象层）
> 来源：[`docs/review.md`](../../../review.md) Part 1 P2 (PAL 抽象层)

## 变更说明

引入 `runtime/src/pal/` 模块作为 z42 Platform Abstraction Layer 的统一入口。
**Phase 1 scope** 收紧到：

- NEW `pal/` 模块骨架（`mod.rs` + `README.md` + per-concern 子模块）
- NEW `docs/design/runtime/pal.md` 长 design doc（CoreCLR 对照 + Phase 2-N
  migration plan + 不变量）
- NEW `pal/system.rs` 提供 `hostname()` / `os_version()` 平台抽象
  （unix / windows / wasm）
- MIGRATE `corelib/system.rs` 走 `pal::system::*` 替代 inline `#[cfg(unix)]`
  / `#[cfg(not(unix))]` 块
- NEW `pal/system_tests.rs` 单元测试

`fs` / `signal` / `thread` / `mem` 等其他 concern 独立 Phase 2-N spec。

## 原因 / Phase 划分

review.md Part 1 P2 完整版（5-7 天）= 把所有散落的 `#[cfg(target_os)]` 集
中到 `pal/`。当前 z42 OS-specific 代码分布：

- `corelib/system.rs` — get_hostname / get_os_version (2 处 cfg)
- `corelib/fs.rs` — make_executable / symlink (4 处 cfg)
- `signal_handler.rs` — 整文件 `#![cfg(unix)]`
- `thread/` — 多线程 stub，无 PAL 支撑

Phase 1 选 **system** 因为：
- 最简单（pure functions，无 state）
- 已有完整 unix + non-unix 分支可以直接迁
- 可作为 Phase 2 migrating fs / signal 的参考模式
- 一个 session 完整闭环 + 写完 design doc

Phase 2 (独立 spec)：迁 `corelib/fs.rs` 4 处 cfg → `pal/fs.rs`
Phase 3 (独立 spec)：迁 `signal_handler.rs` → `pal/signal.rs`
Phase 4 (独立 spec)：建 `pal/thread.rs` 作为 multi-thread 前置
Phase 5 (独立 spec)：建 `pal/mem.rs` 为 GC bump allocator 等做底层抽象

## 文档影响

- NEW `docs/design/runtime/pal.md` — 长期规范
- `docs/review.md` Part 1 P2 状态更新（🟡 Phase 1 done）

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/runtime/src/pal/mod.rs` | NEW | module 入口 + 子模块 re-exports |
| `src/runtime/src/pal/system.rs` | NEW | `hostname()` / `os_version()` + unix / non-unix 实现 |
| `src/runtime/src/pal/system_tests.rs` | NEW | smoke tests (hostname returns non-empty on unix; os_version non-empty) |
| `src/runtime/src/pal/README.md` | NEW | 快速 orientation，指向 design doc |
| `src/runtime/src/lib.rs` | MODIFY | 加 `pub mod pal;` |
| `src/runtime/src/corelib/system.rs` | MODIFY | 删 inline `get_hostname` / `get_os_version`，改 call `pal::system::*` |
| `docs/design/runtime/pal.md` | NEW | 长 design doc (CoreCLR 对照 + migration plan + 不变量) |
| `docs/review.md` | MODIFY | Part 1 P2 / 总表更新 |

只读引用：
- `src/runtime/src/corelib/fs.rs` — Phase 2 迁移目标，design doc 引用
- `src/runtime/src/signal_handler.rs` — Phase 3 目标
- CoreCLR `src/coreclr/pal/` — 设计参考

## 设计要点

### Phase 1 surface (system module)

```rust
// src/runtime/src/pal/system.rs
/// Return the host machine's network hostname. None on platforms where
/// the syscall failed or isn't implemented.
pub fn hostname() -> Option<String>;

/// Return the OS version string (e.g. `"Darwin 24.6.0"`). Empty string
/// on platforms where the syscall fails.
pub fn os_version() -> String;
```

`#[cfg(unix)]` impl 走 libc::gethostname / libc::uname；`#[cfg(not(unix))]`
返回 `None` / `""`（保持现有 corelib/system.rs 的 graceful-degrade 行为）。

### CoreCLR 平行

CoreCLR `pal/` ~50 files 抽象 thread / file / signal / mem / clock。z42
Phase 1 只做 `system` 一块；后续 phases 跟 CoreCLR 的 concern 划分一致。

### 不变量

design doc 详细写：
1. PAL 层只暴露 OS-neutral signatures（return types 都不带 OS 类型）
2. 内部 `#[cfg(...)]` 切分实现，调用方零 cfg
3. 每个新 concern = 一个新 `pal/<concern>.rs` 文件，不混用
4. 错误用 `Option<T>` / `Result<T>` 的 OS-neutral 形式（不暴露 libc::errno）

## 任务

- [x] 0.1 NEW spec `tasks.md`
- [x] 1.1 NEW `pal/mod.rs` + `pal/README.md`
- [x] 1.2 NEW `docs/design/runtime/pal.md` (CoreCLR 对照 + Phase 2-5 migration plan + 不变量)
- [x] 1.3 NEW `pal/system.rs` + `pal/system_tests.rs`（3 tests pass）
- [x] 1.4 MODIFY `lib.rs` add `pub mod pal;`
- [x] 1.5 MODIFY `corelib/system.rs` call `pal::system::*`，删 inline cfg 块 ~45 lines
- [x] 1.6 VERIFY `cargo build --release` clean + `cargo test --lib pal::` 3/3 pass
- [x] 1.7 VERIFY runtime 全 lib tests 770/770 + 21/21 pass（含 corelib::system 7/7）
- [x] 1.8 MODIFY `review.md` 标 🟡 Phase 1 done (2 处)
- [x] 1.9 归档 + commit + push
