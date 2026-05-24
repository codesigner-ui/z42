# Spec: GC Pause-Time Rolling Window

## ADDED Requirements

### Requirement: `PauseHistogram::recent_pauses` field

#### Scenario: Empty heap has empty window
- **WHEN** `PauseHistogram::default()` is constructed
- **THEN** `recent_pauses.len() == 0`
- **AND** `recent_pauses.capacity() >= window_cap`

#### Scenario: Record appends to window in chronological order
- **WHEN** `histogram.record(p1); histogram.record(p2); histogram.record(p3)`
- **THEN** `recent_pauses.iter().collect::<Vec<_>>() == vec![&p1, &p2, &p3]`
- **AND** `recent_pauses.len() == 3`

#### Scenario: Window evicts oldest at capacity (FIFO)
- **GIVEN** window capacity `N`
- **WHEN** `histogram.record(...)` is called `N + K` times (K > 0)
- **THEN** `recent_pauses.len() == N`
- **AND** the first element is the `(K+1)`-th-to-most-recent sample
- **AND** the last element is the most-recent sample

### Requirement: Capacity from `Z42_GC_PAUSE_WINDOW` env

#### Scenario: Default capacity is 1024
- **WHEN** `Z42_GC_PAUSE_WINDOW` is unset
- **THEN** `pause_window_cap_from_env() == PAUSE_WINDOW_DEFAULT_CAP`
- **AND** `PAUSE_WINDOW_DEFAULT_CAP == 1024`

#### Scenario: Env value is clamped into `[1, 65536]`
- **WHEN** `Z42_GC_PAUSE_WINDOW=42`
- **THEN** `pause_window_cap_from_env() == 42`

- **WHEN** `Z42_GC_PAUSE_WINDOW=99999999`
- **THEN** capacity clamps to `65536`

- **WHEN** `Z42_GC_PAUSE_WINDOW=0` or `=-5` or `=garbage`
- **THEN** capacity falls back to `PAUSE_WINDOW_DEFAULT_CAP`

### Requirement: `Std.GC.RecentPauses()` z42 builtin

#### Scenario: Returns chronological long array
- **WHEN** `Std.GC.RecentPauses()` is called from z42 script
- **THEN** returns a `long[]` (oldest first; most-recent last)
- **AND** `result.Length` equals `PauseHistogram::recent_pauses.len()`
- **AND** values are µs measurements (same unit as `pause_us`)

#### Scenario: Empty heap returns empty array
- **WHEN** no `force_collect` has run since heap creation
- **THEN** `result.Length == 0`

#### Scenario: Window saturation does not exceed capacity
- **GIVEN** capacity `N`
- **WHEN** `force_collect` runs `> N` times
- **THEN** `RecentPauses().Length == N`

### Requirement: `Std.GC.PauseWindowCapacity()` z42 builtin

#### Scenario: Returns the configured capacity
- **WHEN** `Std.GC.PauseWindowCapacity()` is called
- **THEN** returns the value selected at heap construction (env or
  default), as `long`
- **AND** the value is in `[1, 65536]`

## MODIFIED Requirements

### Requirement: `PauseHistogram` derives

**Before**: `#[derive(Debug, Clone, Copy, PartialEq, Eq)]`.

**After**: `#[derive(Debug, Clone, PartialEq, Eq)]` (no `Copy`).
Adding `VecDeque<u64>` removes `Copy`. All existing callers use
`Clone` via `HeapStats::stats()`; none rely on `Copy` semantics.
`HeapStats` likewise drops `Copy` (was transitive on
`PauseHistogram`).

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不变
- [x] VM interp — 不变（走 dispatch table）
- [x] JIT — 不变
- [x] GC subsystem — `types.rs` 加 field + env helper；`PauseHistogram::record`
  扩展（append + pop_front）
- [x] corelib — 2 个新 builtins（append 末尾）
- [x] stdlib — `Std.GC.z42` 加 2 个 extern 声明

## IR Mapping

无新 IR 指令。
