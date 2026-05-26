# Tasks: Thread.Sleep(ms) — blocking sleep

> 状态：🟢 已完成 | 创建：2026-05-27 | 归档：2026-05-27

**变更说明：** `Std.Threading.Thread` 加 `public static void Sleep(long millis)` —— 阻塞当前线程指定毫秒。

**原因：** z42.threading 当前只有 `Start` / `Join`，无 `Sleep`。脚本里轮询 / 简单延时只能 busy-wait（CPU 飙满）。HttpClient timeout smoke test 实施期被迫写 `while (i < 30000000) { i = i + 1; }`，丑且不稳。

**类型：** 最小化（1 个新 Rust native + 1 个 stdlib 方法，无 lang change）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/threading.rs` | MODIFY | 加 `builtin_thread_sleep`（~5 LOC） |
| `src/runtime/src/corelib/mod.rs` | MODIFY | BUILTINS 末尾追加 `__thread_sleep` |
| `src/libraries/z42.threading/src/Thread.z42` | MODIFY | 加 `public static void Sleep(long millis)` + `ThreadNative.Sleep` extern |
| `src/libraries/z42.threading/tests/thread_sleep.z42` | NEW | 3 [Test]：基本 sleep / 0 ms no-op / 负值视作 0 |
| `src/libraries/z42.threading/README.md` | MODIFY | API 表加 Sleep |

## Tasks

- [x] 1.1 Rust `builtin_thread_sleep(ms: i64)`：`std::thread::sleep(Duration::from_millis(max(0, ms) as u64))`。负值 saturate 到 0（与 BCL 一致）
- [x] 1.2 `mod.rs` 注册 `__thread_sleep`
- [x] 1.3 z42 `Thread.Sleep(long millis)` + `ThreadNative.Sleep` extern
- [x] 1.4 测试：
  - sleep 100ms，elapsed 在 [80, 500] 范围（OS 调度容忍）
  - sleep 0 立即返回（elapsed < 50ms）
  - sleep -10 立即返回（视作 0）
- [x] 1.5 README + smoke + commit

## 备注

- 不引入 `TimeSpan` 重载（`Std.Time.TimeSpan` 存在但跨包依赖不必为 sleep 引入）；用户可写 `Thread.Sleep(span.TotalMilliseconds())`。
- 不引入 `CancellationToken`（z42 暂无）；纯阻塞 sleep 是 v0 形态。
- 不在 thread-pool 上下文做特殊处理（z42 还没 thread pool）。
- 实现走 `std::thread::sleep`，POSIX `nanosleep` 底层；ms 精度足够脚本场景。
