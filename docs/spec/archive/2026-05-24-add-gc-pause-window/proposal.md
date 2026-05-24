# Proposal: GC Pause-Time Rolling Window

## Why

B5 (add-gc-pause-histogram) provides cumulative distribution since
heap creation. For a long-running server that has been up for weeks,
this answers "what's the all-time pause-time profile?" but not "is
the GC misbehaving **right now**?" — the cumulative numbers smear over
years of operation and bury any recent regression.

Production observability needs a **rolling window**: "what were the
last N pause measurements?". From those, the user can compute their
own p50/p95/p99 over the window, detect a sudden trend break, alert
on a single >X ms pause, etc. — without waiting for the cumulative
numbers to drift.

Pure observability addition. No GC algorithm change. Builds directly
on the existing `PauseHistogram::record` hook.

## What Changes

- `PauseHistogram` gains a `recent_pauses: VecDeque<u64>` field of
  capacity `N` (default `1024`, env-overridable via
  `Z42_GC_PAUSE_WINDOW`)
- `PauseHistogram::record(pause_us)` also pushes the new sample to
  `recent_pauses`, dropping the oldest if the deque is at capacity
- New z42 builtin `Std.GC.RecentPauses() -> long[]` returns the
  current window contents as a `long[]` (oldest first, chronological
  order; up to `N` elements; empty if no collects yet)
- `Std.GC.PauseWindowCapacity() -> long` returns the configured `N`
  (useful for scripts that want to know "are we at saturation?")

Default behavior unchanged: histogram + min/max/total/count still
record exactly as today. The window is additive metadata.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/types.rs` | MODIFY | `PauseHistogram` 加 `recent_pauses: VecDeque<u64>` 字段；`Default`/`record` 维护；新 const `PAUSE_WINDOW_DEFAULT_CAP` + `pause_window_cap_from_env()` helper（读 `Z42_GC_PAUSE_WINDOW`，fallback 1024）|
| `src/runtime/src/gc/types_tests.rs` | MODIFY | 加 `recent_pauses` 单测：FIFO 行为、capacity 上限、env override、`record` 同时更新 bucket 与 window |
| `src/runtime/src/corelib/gc.rs` | MODIFY | 加 `builtin_gc_recent_pauses` + `builtin_gc_pause_window_capacity` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 2 个新 builtins（append 末尾） |
| `src/libraries/z42.core/src/GC/GC.z42` | MODIFY | 加 `Std.GC.RecentPauses(): long[]` + `Std.GC.PauseWindowCapacity(): long` 声明 + 文档 |
| `src/libraries/z42.io/tests/gc_pause_window.z42` | NEW | 端到端：alloc → ForceCollect 几次 → `RecentPauses()` 数量等于 collect 次数 + 长度 ≤ capacity |
| `docs/design/runtime/gc.md` | MODIFY | "Pause histogram" 段加 "rolling window" 小节 + Phase 表加行 + Deferred 段把 `add-gc-pause-window` 标 landed |

**只读引用**：

- `src/runtime/src/gc/types.rs` 现有 `PauseHistogram` 形状
- `src/runtime/src/corelib/gc.rs` 现有 `builtin_gc_pause_*` 模式
- `src/libraries/z42.core/src/GC/GC.z42` 现有 `Std.GC.PauseHistogram`
  / `PauseStatsRaw` 风格

## Out of Scope

- Per-mode rolling windows（每个 GcMode 独立 deque）：v1 单 window
  跨 mode 累积，per-mode 在 `add-gc-pause-per-mode` 落地后再拆
- 时间戳标注每个 pause 样本：当前只存 `pause_us`，不存"发生于何时"。
  时间序列分析（trend break detection）等独立 spec
- 显式 reset window API（手动清空）：cumulative 数据 + 用户记录上次
  调用时的 deque 内容 + 自己 diff 即可
- 与 OpenTelemetry / Prometheus 集成（这是 `add-gc-pause-trace-export`
  的范围）

## Open Questions

无。
