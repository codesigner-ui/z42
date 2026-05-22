# Proposal: GC Pause-Time Histogram

## Why

Each `collect_cycles` already measures `pause_us` (microseconds) and
emits it inside `GcEvent::AfterCollect`. Currently this is only
visible to programs that install a `GcObserver` and inspect each
event. There's no aggregate view: "what's the p95 pause time over
the last 10000 collects?" / "did concurrent mode actually reduce
median pause?" / "which mode best fits this workload?" — all
questions a user has when tuning `Z42_GC_MODE`.

This spec adds a **fixed-bucket histogram** that aggregates every
collect's `pause_us` into 8 logarithmic-ish buckets:

```
[0, 10) µs    | [10, 100) µs   | [100µs, 1ms)  | [1, 10) ms
[10, 100) ms  | [100ms, 1s)    | [1s, 10s)     | [10s+, ∞)
```

Plus min / max / total / count for basic stats. Aggregated into
`HeapStats` (already a public read-only struct) so it surfaces
through `heap.stats()` AND the existing `Std.GC.GetStats()` builtin —
no new public API needed in v1. A new `Std.GC.PauseHistogram()`
builtin exposes the raw bucket counts as an int array (`[u64; 8]`
projected as z42 long array) for scripts that want fine-grained
analysis.

Builds on existing `pause_us` instrumentation. No GC algorithm
changes, no behavioral change — pure observability addition.

## What Changes

- New type `PauseHistogram { buckets: [u64; 8], min_us: u64, max_us:
  u64, total_us: u64, count: u64 }` in `gc/types.rs`
- `ArcMagrGC` adds `pause_histogram: Mutex<PauseHistogram>` field
- Each `collect_cycles` / `collect_cycles_with_context` records its
  measured `pause_us` into the histogram at the same point it fires
  `AfterCollect`
- `HeapStats` gains a `pause_histogram: PauseHistogram` field; `stats()`
  reads the histogram once into the stats snapshot
- New `Std.GC.PauseHistogram()` z42-script builtin returns the raw
  bucket counts as `long[]` of length 8; documented bucket boundaries
  in the script-side doc-comment
- New builtin `Std.GC.PauseStatsRaw()` returns `(min_us, max_us,
  total_us, count)` as a 4-element `long[]` (no separate struct type
  to avoid new TypeDesc allocation)
- `Std.GC` z42-script API surface: 2 new methods (`PauseHistogram() →
  long[]`, `PauseStatsRaw() → long[]`)
- Documentation: `gc.md` "Pause histogram" subsection right after
  "Stress testing"

**Default behavior unchanged**: histogram recording is O(1) per
collect (log2 bucket lookup + atomic-counted u64 increments via
Mutex). Aggregation is opt-in via the new API calls; existing
`HeapStats` consumers see the new field but it's harmless if ignored.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/types.rs` | MODIFY | Add `PauseHistogram` struct (8 buckets + min/max/total/count). Add to `HeapStats`. `#[derive(Debug, Clone, Default)]`. |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | Add `pause_histogram: Mutex<PauseHistogram>` field; record pause at end of each collect path; `stats()` copies it into HeapStats. |
| `src/runtime/src/gc/arc_heap_tests/pause_histogram.rs` | NEW | Unit tests: bucket assignment correct for boundary values; min/max/total/count update correctly; histogram preserved across mode switches. |
| `src/runtime/src/gc/arc_heap_tests/mod.rs` | MODIFY | Register `pause_histogram` module. |
| `src/runtime/src/corelib/gc.rs` | MODIFY | Add `builtin_gc_pause_histogram` + `builtin_gc_pause_stats_raw` — project HeapStats.pause_histogram into z42 `long[]`. |
| `src/runtime/src/corelib/mod.rs` | MODIFY | Register `__gc_pause_histogram` + `__gc_pause_stats_raw` builtins. |
| `src/libraries/z42.core/src/GC/GC.z42` | MODIFY | Expose `Std.GC.PauseHistogram(): long[]` + `Std.GC.PauseStatsRaw(): long[]` with doc-comments documenting bucket boundaries + return semantics. |
| `docs/design/runtime/gc.md` | MODIFY | New "Pause histogram" subsection; Phase table row; B5 backlog entry "future" → "landed". |

**只读引用**：existing `pause_us` instrumentation in collect paths,
HeapStats projection in `builtin_gc_stats`.

## Out of Scope

- **t-digest / accurate percentiles**: fixed buckets give approximate
  p50/p95/p99 from bucket counts, sufficient for v1. True t-digest
  would be a follow-up perf spec if precision is needed.
- **Per-mode histogram split**: single histogram aggregates all modes.
  Users who care about per-mode comparison can call `PauseHistogram`,
  `set_mode`, force-some-collects, `PauseHistogram` again, diff.
  Per-mode split is future work.
- **Reset / rolling-window histogram**: histogram accumulates across
  the heap's lifetime. Reset API + sliding-window (e.g. "last 1000
  collects") are future specs if needed for long-running processes.
- **Pause-time SLA enforcement**: hooks like "panic if any collect
  exceeds 100ms" are out of scope; users can poll via the API.
- **Distributed tracing integration** (OpenTelemetry / etc.): export
  to external tracers is a future spec.

## Open Questions

无。
