# Spec: Multi-collector arbitration

## ADDED Requirements

### Requirement: Only one thread can be the active collector at a time

#### Scenario: Single-collector path unchanged
- **WHEN** thread A calls `request_gc_pause(ctx)` and no other collector
  is active
- **THEN** returns `Some(GcPauseGuard)`, sets `collector_active = true`,
  sets `gc_phase = Requested`, waits for `parked_count >= vm_contexts.len() - 1`,
  transitions to `Marking`, returns guard

#### Scenario: Second concurrent collector parks as mutator
- **WHEN** thread A is mid-collect (holds the GcPauseGuard, phase = Marking);
  thread B calls `request_gc_pause(ctx)`
- **THEN** B's `compare_exchange(false, true)` on `collector_active` fails;
  B calls `park_until_idle(ctx)` (parks until A drops the guard); returns
  `None`. B does NOT call `collect_cycles` itself.

#### Scenario: After active collector finishes, next request can succeed
- **WHEN** A finishes collect and drops its `GcPauseGuard`; collector_active
  flips back to false; thread C calls `request_gc_pause(ctx)`
- **THEN** C's CAS succeeds; C becomes the new active collector

### Requirement: 4-worker auto-collect test passes

#### Scenario: 4 workers tight-budget alloc + 1 main, no deadlock
- **WHEN** 4 worker threads each loop alloc + check_safepoint with
  `max_heap_bytes = 8KB` (auto-threshold trips repeatedly across workers)
- **THEN** all workers complete; auto-collect fires at least once;
  `gc_cycles > 0`; no deadlock

### Requirement: Concurrent explicit Std.GC.Collect arbitrates

#### Scenario: 2 threads call `Std.GC.Collect()` simultaneously
- **WHEN** main and worker thread both invoke `builtin_gc_collect`
  (z42 `Std.GC.Collect()`)
- **THEN** only one runs the actual collect; the other observes another
  collector is active and silently returns (no-op). No deadlock, no
  double-collect data race.

## MODIFIED Requirements

### Requirement: `request_gc_pause` return type

**Before:** `pub fn request_gc_pause(ctx: &VmContext) -> GcPauseGuard<'_>`
(always succeeds)

**After:** `pub fn request_gc_pause(ctx: &VmContext) -> Option<GcPauseGuard<'_>>`
(None when another collector is active; caller already park-as-mutator'd
inside the function)

## IR Mapping

No new IR. Internal protocol change.

## Pipeline Steps

- [x] VM interp — `check_safepoint_slow` handles Option None branch
- [x] VM JIT — same (calls same Rust functions via trampoline)
