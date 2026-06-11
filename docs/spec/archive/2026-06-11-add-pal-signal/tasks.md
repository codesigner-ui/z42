# Tasks: add-pal-signal (PAL Phase 3)

**变更说明：** 抽 `signal_handler.rs` 的 OS 原语（fatal-signal 注册 + async-signal-safe write + signal_name + reset/reraise）→ 新 `pal/signal.rs`；z42 崩溃 reporter（handler / write_call_stacks，知道 VM_CORES）留在 signal_handler.rs 调 pal::signal。
**原因：** PAL Phase 3。**修正 pal.md 与 PAL 不变量冲突**：原计划「整文件迁移」会把 z42 runtime 内省塞进 pal/，违反 PAL「OS-neutral surface」——User 2026-06-11 裁决走正确抽取。
**类型：** refactor（行为保持，async-signal-safe 约束不变，无格式 bump）。
**文档影响：** `pal.md`（Phase 3 计划修正 + done）+ `pal/README.md`。
**子系统锁：** runtime。

## 边界（OS 原语 vs z42 逻辑）

| → `pal/signal.rs`（OS 原语，async-signal-safe，零 z42 知识） | 留 `signal_handler.rs`（z42 崩溃逻辑） |
|---|---|
| `register_fatal_handlers(handler: extern "C" fn(i32))`（5 fatal 信号注册循环）| `install()`（open_crash_fd + 调 pal::register）|
| `signal_name(sig) -> &[u8]` | `handler` / `write_banner` / `write_call_stacks`（走 VM_CORES）|
| `reset_default_and_reraise(sig)`（SIG_DFL + raise）| `open_crash_report_fd` / `SIGNAL_CRASH_FD` |
| `pub mod sigsafe { write_str / write_dec_u32 / write_hex_u64 }` | |

## 任务

- [x] 1. 新建 `pal/signal.rs`（`#![cfg(unix)]`）：register_fatal_handlers + signal_name + reset_default_and_reraise + sigsafe（原语原样搬）
- [x] 2. 新建 `pal/signal_tests.rs`：sigsafe + signal_name 测试（从 signal_handler_tests 搬）
- [x] 3. `pal/mod.rs`：`#[cfg(unix)] pub mod signal;` + doc（signal future→current）
- [x] 4. 重写 `signal_handler.rs`：删搬走的原语，handler/install/write_* 改调 `crate::pal::signal::{*, sigsafe}`；保持 `#![cfg(unix)]` + async-signal-safe 约束
- [x] 5. `signal_handler_tests.rs`：只留 install_is_idempotent
- [x] 6. 文档：`pal.md` Phase 3 计划改正确设计 + ✅ landed；`pal/README.md` 加 signal.rs
- [x] 7. GREEN：cargo build 759 + pal::signal 9 单测（sigsafe/signal_name）+ signal_handler install_idempotent 全过。**e2e（signal_handler_e2e 真崩溃路径）被 pre-existing 5-天陈年 `signal_crash_helper` UE 僵尸 + macOS crash-exit 重载卡 UE 堵塞（见 memory reference_xtask_gate_zombie_jam）→ 非本变更回归**：抽取是 verbatim（sigsafe/signal_name 字节级原样搬、handler 不变、reraise verbatim），行为保持由构造保证 + 单测覆盖
- [x] 8. commit + push + 释锁归档

## 备注

main.rs 对 `signal_handler::install()` 的调用 + gating **不动**（signal_handler.rs 公开
surface 不变）。Windows VEH（Phase 3.1）走 pal::signal 同接口不同 impl，仍延后。
async-signal-safe 正确性：sigsafe 原语字节级原样搬、handler 结构不变，无行为/约束变化。
