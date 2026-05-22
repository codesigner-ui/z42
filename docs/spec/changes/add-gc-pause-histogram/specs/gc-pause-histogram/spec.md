# Spec: GC Pause-Time Histogram

## ADDED Requirements

### Requirement: PauseHistogram type

#### Scenario: Empty histogram has zero state
- **WHEN** `PauseHistogram::default()` is constructed
- **THEN** all `buckets[0..8]` are 0
- **AND** `min_us = u64::MAX`, `max_us = 0`, `total_us = 0`, `count = 0`

#### Scenario: Record updates bucket + min/max/total/count
- **WHEN** `histogram.record(pause_us)` is called
- **THEN** the bucket index corresponding to `pause_us` is incremented
- **AND** `min_us` becomes the min of current and `pause_us`
- **AND** `max_us` becomes the max of current and `pause_us`
- **AND** `total_us += pause_us`
- **AND** `count += 1`

### Requirement: Bucket boundaries match spec

#### Scenario: 8 buckets logarithmically distributed
- **GIVEN** bucket boundaries `[10, 100, 1_000, 10_000, 100_000,
  1_000_000, 10_000_000]` (microseconds)
- **WHEN** `bucket_index(pause_us)` is called for various pause values
- **THEN** returns:
  - `0` for `pause_us < 10`           (< 10 µs)
  - `1` for `10 ≤ pause_us < 100`     ([10, 100) µs)
  - `2` for `100 ≤ pause_us < 1_000`  ([100µs, 1ms))
  - `3` for `1_000 ≤ pause_us < 10_000` ([1, 10) ms)
  - `4` for `10_000 ≤ pause_us < 100_000` ([10, 100) ms)
  - `5` for `100_000 ≤ pause_us < 1_000_000` ([100ms, 1s))
  - `6` for `1_000_000 ≤ pause_us < 10_000_000` ([1, 10) s)
  - `7` for `pause_us ≥ 10_000_000`   (≥ 10s)

#### Scenario: Boundary values fall into the higher bucket
- **WHEN** `pause_us == 10` (boundary)
- **THEN** lands in bucket 1, not bucket 0 (half-open intervals
  `[lower, upper)`)

### Requirement: ArcMagrGC integration

#### Scenario: Every collect records its pause
- **WHEN** `collect_cycles` (or any GcMode-specific variant) completes
- **THEN** the measured `pause_us` for that cycle is recorded into
  `pause_histogram`
- **AND** `stats().pause_histogram` reflects the updated counts

#### Scenario: Histogram persists across mode switches
- **WHEN** `set_mode` is called between collects
- **THEN** the histogram is NOT reset
- **AND** counts from prior modes remain in their buckets

### Requirement: Std.GC.PauseHistogram() z42 builtin

#### Scenario: Returns 8-element long array
- **WHEN** `Std.GC.PauseHistogram()` is called from z42 script
- **THEN** returns a `long[]` of exactly 8 elements
- **AND** `result[i]` is the count for bucket `i` (indices 0..8 per
  the bucket boundaries above)
- **AND** values are non-negative monotonically-growing counters

### Requirement: Std.GC.PauseStatsRaw() z42 builtin

#### Scenario: Returns [min_us, max_us, total_us, count]
- **WHEN** `Std.GC.PauseStatsRaw()` is called
- **THEN** returns a `long[]` of exactly 4 elements `[min_us, max_us,
  total_us, count]`
- **AND** when no collects have occurred yet: `min_us = i64::MAX`,
  `max_us = 0`, `total_us = 0`, `count = 0`. (Sentinel for "empty";
  callers check `count == 0` to skip the min/max sentinel.)

## MODIFIED Requirements

### Requirement: HeapStats structure

**Before** (current): 7 fields — `allocations`, `gc_cycles`,
`used_bytes`, `max_bytes`, `roots_pinned`, `finalizers_pending`,
`observers`.

**After**: 8 fields — adds `pause_histogram: PauseHistogram`. The
`stats()` method copies the current histogram snapshot into the
returned `HeapStats`. Existing consumers don't need to read the new
field — `#[derive(Debug, Clone, Default)]` keeps the struct
default-constructible. Adding a struct field is source-compatible
for `HeapStats { foo, bar, .. }` patterns; named-field access via
`stats.allocations` etc. unaffected.

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不变
- [x] VM interp — 不变
- [x] JIT — 不变
- [x] GC subsystem — types.rs adds struct + field; arc_heap.rs records
  + projects via stats(); corelib/gc.rs adds 2 builtins
- [x] stdlib — Std.GC.z42 adds 2 method declarations

## IR Mapping

无新 IR 指令。
