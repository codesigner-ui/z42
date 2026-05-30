# Proposal: Std.Threading.Timer — periodic + one-shot background callback

## Why

`docs/design/stdlib/roadmap.md` Deferred 行 `time-future-sleep-timer` 已
列出 Timer 为 follow-up，但 v0 一直延后到 "async/await 或阻塞 Sleep
syscall"。后者 (`Std.Threading.Thread.Sleep`) **已落地 2026-05-27**
(add-thread-sleep)；前者不必等。Timer 现在可纯脚本 over Thread + Sleep
落地，无需新 VM builtin。

应用层用例：

- 周期性 health check / heartbeat（每 30s 检查 dependency 状态）
- 缓存 TTL eviction（每 60s 扫一轮）
- one-shot 延迟动作（30s 后无 ack 则 retry）
- 周期 metrics flush

不做的话，调用方手写 `Thread.Start(() => { while (!stopped[0]) {
Thread.Sleep(ms); callback(); } })` —— 每个用例重复 5–10 行同样的代码，
且容易遗漏 W0604 cell pattern 导致 silent bug（昨天我自己刚踩过）。

## What Changes

新增 `Std.Threading.Timer` 类（pure z42, no new VM builtin）：

```z42
public class Timer {
    public static Timer StartPeriodic(long intervalMs, Action callback);
    public static Timer StartOnce(long delayMs, Action callback);
    public void Stop();              // signal + chunked-sleep wakes within ≤100ms
    public void StopAndJoin();       // Stop then wait for current callback to finish
    public bool IsRunning();
}
```

实现：

- 背景 thread 用 `Std.Threading.Thread.Start`
- Stop 信号经 `bool[1]` cell（W0604 推荐模式）
- 背景 thread 用 100ms 颗粒度 chunked sleep — Stop 响应 ≤100ms 无论
  intervalMs 多长
- Periodic：fire-sleep-check-loop，每次 callback 完毕后再 sleep（无重叠
  调用，避免长 callback 触发 race）
- Once：sleep delayMs（可中断）→ 若仍未 Stop，fire callback 一次 → 自动
  Stop
- Callback 抛异常会被 swallow + log via `Std.Diagnostics.Log.Error`（不让
  一次失败杀掉 Timer 线程）

依赖：z42.threading 已有 Thread + Sleep + Action。再加 z42.diagnostics
作 dep for Log.Error。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.threading/src/Timer.z42` | NEW | Timer 类 + 静态 factory + Stop/Join/IsRunning |
| `src/libraries/z42.threading/z42.threading.z42.toml` | MODIFY | 加 z42.diagnostics dep |
| `src/libraries/z42.threading/tests/timer_basic.z42` | NEW | 8 个单测 |
| `docs/design/stdlib/time.md` 或 `docs/design/stdlib/threading.md` | MODIFY | API 文档 |
| `docs/design/stdlib/roadmap.md` | MODIFY | Deferred 行 `time-future-sleep-timer` Timer 部分标 ✅ |

**只读引用**：

- `src/libraries/z42.threading/src/Thread.z42`（Thread.Start / Thread.Sleep 形态）
- `src/libraries/z42.diagnostics/src/Log.z42`（Log.Error API）

## Out of Scope

- High-precision Timer（sub-ms）— OS Sleep 颗粒度限制；用 hardware timer / spinlock 类需求开独立 spec
- Scheduled-time API（"fire at 2026-12-01 09:00 UTC"）— 需要 calendar
  arithmetic + DateTime（已有），独立 spec
- Multi-callback Timer / event 多播 — 用 Action 多播或单独 spec
- 跨 Timer 共享 thread pool — v0 一 Timer 一 thread；高 Timer 数场景留
  `timer-future-shared-worker` follow-up

## Open Questions

- [ ] StopAndJoin 是否同步等 callback 当前调用结束？
  **决定**：是 — 否则 Stop 后用户立即 Dispose 资源，callback 仍在跑就 race
- [ ] Stop 后再 Stop 是否抛？
  **决定**：不抛（idempotent）— 与 z42.net Dispose 模式一致
- [ ] callback 抛异常的行为？
  **决定**：swallow + Log.Error；不让 Timer 线程因一次 callback fail 而死
