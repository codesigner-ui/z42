# Spec: GC Debug-Only Invariant Checks

## ADDED Requirements

### Requirement: Region<T> invariant validator

#### Scenario: Healthy region passes all checks
- **GIVEN** a `Region<T>` with N alive entries, M tombstoned entries
  in `free_list`, and a correctly-maintained `young_list`
- **WHEN** `region.validate()` is called
- **THEN** the result is `Ok(())`

#### Scenario: young_list contains only young entries
- **WHEN** the validator finds an entry `(ci, ei)` in `young_list`
  with `gen_age >= PROMOTION_THRESHOLD`
- **THEN** the result is `Err(Violation::OldEntryInYoungList { chunk_idx, entry_idx, gen_age })`

#### Scenario: All young alive entries are in young_list
- **WHEN** the validator finds an alive entry with
  `gen_age < PROMOTION_THRESHOLD` that does NOT appear in `young_list`
- **THEN** the result is `Err(Violation::YoungEntryNotInList { chunk_idx, entry_idx })`

#### Scenario: young_list contains no duplicates
- **WHEN** `young_list` contains the same `(ci, ei)` twice
- **THEN** the result is `Err(Violation::DuplicateInYoungList { chunk_idx, entry_idx })`

#### Scenario: free_list slots are tombstoned
- **WHEN** the validator finds a slot `(ci, ei)` in `free_list` with
  `alive == true`
- **THEN** the result is `Err(Violation::AliveSlotInFreeList { chunk_idx, entry_idx })`

#### Scenario: Self-location matches storage location
- **WHEN** the validator finds an initialized entry at `chunks[ci][ei]`
  whose `entry.location != (ci, ei)`
- **THEN** the result is `Err(Violation::LocationMismatch { expected, found })`

#### Scenario: card_dirty has one entry per chunk
- **WHEN** `card_dirty.len() != chunks.len()`
- **THEN** the result is `Err(Violation::CardDirtyLengthMismatch { expected, actual })`

### Requirement: ArcMagrGC heap-wide invariant validator

#### Scenario: Healthy heap passes all checks
- **GIVEN** an `ArcMagrGC` post-collect with both regions valid and
  `mark_queue` empty
- **WHEN** `heap.debug_validate_invariants()` is called
- **THEN** no panic, validation succeeds

#### Scenario: Stale mark bits panic
- **WHEN** after `sweep_phase` completes, the validator finds any
  alive entry with `marked == 1` (mark bit not cleared)
- **THEN** validation panics with a descriptive message

#### Scenario: Stale mark_queue panics
- **WHEN** outside an active concurrent mark cycle, `mark_queue.lock()`
  is non-empty
- **THEN** validation panics

### Requirement: Validation runs at end of every collect (debug builds)

#### Scenario: Default collect path triggers validation
- **WHEN** `collect_cycles()` completes (any GcMode)
- **AND** the build was compiled with `debug_assertions`
- **THEN** `debug_validate_invariants` is invoked
- **AND** any invariant violation panics

#### Scenario: Release build skips validation
- **WHEN** `collect_cycles()` completes in a release build
- **THEN** no validation runs (zero overhead)
- **AND** the validation method body compiles away entirely

### Requirement: Test access to validator

#### Scenario: Tests can call validate() and inspect Result
- **GIVEN** a test that intentionally corrupts heap state
- **WHEN** the test calls `region.validate()` directly
- **THEN** it gets `Err(Violation)` with the specific failure type,
  enabling assertions on the violation variant

## MODIFIED Requirements

### Requirement: collect_cycles control flow

**Before**: After mark + sweep, `collect_cycles` updates stats and
fires the `AfterCollect` event.

**After**: Same, plus a final `#[cfg(debug_assertions)]
self.debug_validate_invariants();` call that asserts the heap is
internally consistent. Release builds compile this out.

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不变
- [x] VM interp — 不变
- [x] JIT — 不变
- [x] GC subsystem — region.rs adds validate(); arc_heap.rs adds debug_validate_invariants(); collect_cycles invokes
- [x] Tests — new arc_heap_tests/invariants.rs

## IR Mapping

无新 IR 指令。
