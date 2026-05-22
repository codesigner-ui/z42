# Tasks: GC Pause-Time Histogram

> 状态：🟡 进行中（spec-only commit start）| 创建：2026-05-22 | 类型：vm

**总体策略**：Pure observability addition. No GC algorithm changes.
8-bucket logarithmic histogram on `pause_us`. Surface via existing
`HeapStats` + 2 new z42-script builtins.

**总工作量估算**：~1 session / ~250 LOC. Small spec.

## 进度概览

- [ ] 阶段 1-6: spec 文档
- [ ] 阶段 6.5: User 确认
- [ ] 阶段 7: 实施 P0
- [ ] 阶段 8: GREEN
- [ ] 阶段 9: 归档

## P0: PauseHistogram + integration + builtins (~1 session)

- [ ] P0.1 `gc/types.rs` 加 `PauseHistogram` struct + 8-bucket
       `BUCKET_EDGES` const + `bucket_index(u64) -> usize` +
       `record(pause_us)` method + `Default` impl with sentinel `min_us
       = u64::MAX`
- [ ] P0.2 `HeapStats` 加 `pause_histogram: PauseHistogram` field
- [ ] P0.3 `ArcMagrGC` 加 `pause_histogram: Mutex<PauseHistogram>` field
- [ ] P0.4 每个 collect path 在 AfterCollect event 前调 `record(pause_us)`：
       - `collect_cycles` (default path)
       - `collect_cycles_with_context` ConcurrentMarkSweep arm
       - `collect_cycles_with_context` GenerationalMarkSweep arm
       (StwMarkSweep arm calls collect_cycles internally → single record)
- [ ] P0.5 `stats()` 复制 histogram into HeapStats
- [ ] P0.6 `builtin_gc_pause_histogram` + `builtin_gc_pause_stats_raw`
       in corelib/gc.rs；注册 `__gc_pause_histogram` + `__gc_pause_stats_raw`
       in corelib/mod.rs
- [ ] P0.7 `Std.GC.z42` 加 `PauseHistogram(): long[]` + `PauseStatsRaw():
       long[]` 声明 + 文档化 bucket boundaries
- [ ] P0.8 单测 `gc/types.rs` 同文件下 mod test 或新 `arc_heap_tests/pause_histogram.rs`：
       - `bucket_index_boundaries`（每个 boundary）
       - `record_updates_bucket`
       - `record_updates_min_max_total_count`
       - `record_first_value_sets_min_from_sentinel`
       - `default_is_empty`
       - `histogram_visible_in_stats_after_collect`
       - `histogram_persists_across_mode_switches`
- [ ] P0.9 (可选) 端到端 stdlib test 验证 `Std.GC.PauseHistogram()`
       返回正确长度 + 非全零（需要先 alloc + force_collect）
- [ ] P0.10 cargo --lib gc:: GREEN
- [ ] P0.11 commit

## P1: gc.md docs + archive (~0.5 session)

- [ ] P1.1 `docs/design/runtime/gc.md` 加新 "Pause histogram"
       subsection 紧跟 "Stress testing" 后:
       - Bucket boundaries 列表
       - 怎么从 script 端读取
       - 怎么对比 mode（diff before/after set_mode）
       - 局限 (fixed buckets, no rolling window, no per-mode split)
- [ ] P1.2 Phase 表加 add-gc-pause-histogram 行
- [ ] P1.3 B5 backlog entry: "future" → "landed"
- [ ] P1.4 archive 到 `docs/spec/archive/YYYY-MM-DD-add-gc-pause-histogram/`
- [ ] P1.5 final `test-all.sh --scope=full` GREEN
- [ ] P1.6 commit + push

## 备注

实施期发现入 commit message + 备注 section.

## 后续 spec 依赖关系

| 后续 spec | 依赖本 spec 的什么 |
|----------|-------------------|
| `add-gc-pause-tdigest` | 精确 percentile (t-digest 替 fixed bucket) |
| `add-gc-pause-per-mode` | 3 individual histograms |
| `add-gc-pause-window` | Rolling window (last N collects) |
| `add-gc-pause-sla` | Hook for SLA violations |
| `add-gc-pause-trace-export` | OpenTelemetry / Jaeger export |
