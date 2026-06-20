# Tasks: fix-flaky-periodic-timer-test

> 状态：🟢 已完成 | 创建：2026-06-21 | 完成：2026-06-21

**变更说明：** `Z42ThreadingTimerTests.test_periodic_fires_multiple_times` 由固定 `Thread.Sleep(500)` 窗口改为轮询等待 `fires >= 3`（上限 ~5s）。
**原因：** 该测试用固定睡眠窗口等 50ms 周期定时器，macos-15 CI runner 超载时定时器线程被饿死 → `fires < 3` → `expected true but got False`。windows 同 run 通过，仅 macos-15 挂 = 典型慢 runner 时序 flaky（2026-06-01 已硬化过一次窗口仍复发）。固定窗口换轮询等待条件：正常 ~150ms 早退，starved runner 给足 slack，只有定时器真死才失败。
**子系统：** `stdlib`（z42.threading；ACTIVE.md 登记）
**文档影响：** 无对外行为变更（测试鲁棒性修复，代码内注释已说明）。

- [x] 1.1 `src/libraries/z42.threading/tests/timer_basic.z42`：固定 sleep → poll-until `fires>=3`（cap 5000ms）
- [x] 1.2 验证：本地 `xtask test stdlib z42.threading` 全绿（12/12 文件，timer_basic 10/10）
- [x] 1.3 归档 + commit + push（CI 确认）
