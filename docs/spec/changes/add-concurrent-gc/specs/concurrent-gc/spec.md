# Spec: Concurrent Mark + Selectable GC Mode

## ADDED Requirements

### Requirement: Runtime-selectable GC mode

#### Scenario: Default mode is STW mark-sweep
- **WHEN** `ArcMagrGC::new()` is called without configuration
- **THEN** `heap.mode()` returns `GcMode::StwMarkSweep`
- **AND** `collect_cycles` / `force_collect` go through the existing STW
  mark-sweep path (no behavior change vs. pre-this-spec)

#### Scenario: Concurrent mode opt-in via API
- **WHEN** `heap.set_mode(GcMode::ConcurrentMarkSweep)` is called
- **THEN** `heap.mode()` returns `ConcurrentMarkSweep`
- **AND** subsequent `collect_cycles` / `force_collect` go through the
  concurrent path

#### Scenario: Concurrent mode opt-in via env var
- **WHEN** the process starts with `Z42_GC_MODE=concurrent` set
- **THEN** newly-constructed `ArcMagrGC::new()` instances default to
  `ConcurrentMarkSweep`
- **AND** `Z42_GC_MODE=stw` or unset → `StwMarkSweep`
- **AND** invalid values fall back to `StwMarkSweep` with a stderr warning

#### Scenario: Mode switch is observable but cannot interrupt a running collect
- **WHEN** a collect is in progress and `set_mode` is called from another thread
- **THEN** the in-progress collect completes using its original mode
- **AND** the next collect uses the new mode

### Requirement: Concurrent mark correctness

#### Scenario: Reachable objects survive concurrent mark
- **WHEN** the heap is in `ConcurrentMarkSweep` mode
- **AND** mutators continue allocating and writing during the mark phase
- **THEN** every object reachable at the *start of mark* PLUS every object
  written into a reachable slot *during mark* is preserved across the
  collect
- **AND** the final sweep frees only objects unreachable at the
  termination handshake

#### Scenario: Write barrier shades new references gray
- **WHEN** mode is `ConcurrentMarkSweep`
- **AND** interp or JIT executes a heap-ref FieldSet / ArraySet
- **AND** the new value is a heap reference (passes the call-site
  `is_heap_ref()` filter from `add-write-barriers`)
- **THEN** the barrier override marks the new value (sets its `marked` bit)
  AND pushes it to the mark queue (if not already marked)
- **AND** the mark thread eventually traces it before sweep begins

#### Scenario: Write barrier is no-op in STW mode
- **WHEN** mode is `StwMarkSweep`
- **AND** any FieldSet / ArraySet occurs
- **THEN** the barrier override is a no-op (mark queue stays empty,
  no atomic mark operation)
- **AND** STW collect cycles produce bit-identical results to
  pre-this-spec baseline

### Requirement: Termination via short STW handshake

#### Scenario: Mark completes via final STW pause
- **WHEN** the concurrent mark thread has drained its queue and observes
  no new gray objects for one iteration
- **THEN** it requests a STW pause (via existing `request_gc_pause`)
- **AND** parks all mutators
- **AND** drains any final-burst gray objects added between drain-check and pause
- **AND** transitions `GcPhase::ConcurrentMarking` → `Sweeping`
- **AND** runs STW sweep
- **AND** releases the pause

#### Scenario: Pre-handshake auto-collect deferral
- **WHEN** a mutator's `maybe_auto_collect` trips the threshold during
  concurrent mark
- **THEN** the mutator does NOT start a second collect (`collector_active`
  CAS fails)
- **AND** the next `check_safepoint` after the current collect completes
  may re-trigger if pressure persists

### Requirement: GcPhase state machine extension

#### Scenario: New ConcurrentMarking phase
- **GIVEN** `GcPhase` enum now has variants `Idle / Requested /
  ConcurrentMarking / Marking / Sweeping` (Marking = STW mark, retained
  for the STW path)
- **WHEN** STW mode is selected → phases flow `Idle → Requested → Marking
  → Sweeping → Idle` (unchanged)
- **WHEN** Concurrent mode is selected → phases flow
  `Idle → Requested → ConcurrentMarking → Marking → Sweeping → Idle`
  (the brief `Marking` STW phase is the termination handshake;
  `ConcurrentMarking` is the long concurrent phase where mutators run)

#### Scenario: Mutator parking respects phase semantics
- **WHEN** phase is `ConcurrentMarking`
- **THEN** mutators do NOT park at safepoints (they run the
  write-barrier override but do not block)
- **WHEN** phase is `Marking` (termination handshake) or `Sweeping`
- **THEN** mutators park as before

### Requirement: Atomic ordering audit

#### Scenario: Mark bit operations are correctness-preserving under concurrent access
- **WHEN** two threads (mark + mutator-via-barrier) race to mark the
  same `GcAllocation`
- **THEN** the CAS-based `GcRef::mark` (already atomic) ensures exactly
  one transitions `0 → 1`; the other returns `false` and skips the
  enqueue (no double-trace)
- **AND** the documented ordering (currently `Relaxed`) is sufficient
  **only because** every cross-thread visibility point goes through
  either the `parking_lot::Mutex` on `mark_queue` (Acquire/Release) or
  the STW handshake transition (Mutex on `gc_phase`, Acquire/Release).
  If a future refactor removes those synchronizing points, the audit
  must be re-done.

## MODIFIED Requirements

### Requirement: `run_cycle_collection` dispatch

**Before** (add-mark-sweep-collector): Always runs `mark_phase()` then
`sweep_phase()` STW.

**After**: Branches on `self.mode()`:
- `StwMarkSweep` → identical to before (mark_phase + sweep_phase STW)
- `ConcurrentMarkSweep` → snapshot roots STW → release pause → run
  background mark thread → termination handshake STW → sweep STW → release pause

### Requirement: Write barrier override semantics

**Before** (add-write-barriers): `ArcMagrGC` overrides barrier methods
to fire `#[cfg(test)] BarrierObserver`. Production path is no-op.

**After**: Override branches on `self.mode()`:
- `StwMarkSweep` → no-op (production); `#[cfg(test)]` still fires observer
- `ConcurrentMarkSweep` → if `new.is_heap_ref()` (caller already filtered;
  override re-asserts via `debug_assert!`), mark `new` via
  `GcRef::mark` CAS; if CAS succeeded (was previously unmarked), push
  to `mark_queue`. `#[cfg(test)]` observer also fires.

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不变
- [x] VM interp — 无 call site 变化（barrier override 内部实现变化）
- [x] JIT — 同 interp
- [x] GC subsystem — 主要变更点
- [x] Safepoint protocol — GcPhase enum 扩展 + 状态转换

## IR Mapping

无新 IR 指令。
