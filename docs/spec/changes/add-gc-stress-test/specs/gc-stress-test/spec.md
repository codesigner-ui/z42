# Spec: GC Random-Workload Stress Test

## ADDED Requirements

### Requirement: Seeded random-workload driver

#### Scenario: Same seed produces identical workload
- **WHEN** `gc_stress_run_seeded(seed, iters, mode)` is called twice
  with the same `(seed, iters, mode)` triplet
- **THEN** both runs execute the same operation sequence (modulo
  thread-scheduling effects on shared mode switches)
- **AND** the sequence is deterministic by seed (using a seedable
  PRNG like `SmallRng::seed_from_u64`)

#### Scenario: Failure surfaces the seed
- **WHEN** a stress run triggers an invariant violation (panic from
  C1 validator) or any other panic
- **THEN** the panic message includes the seed value so the failure
  can be reproduced via `Z42_STRESS_SEED=<seed> cargo test ...`

### Requirement: Operation coverage

#### Scenario: Workload exercises all 5 op categories
- **GIVEN** a stress run of N iterations
- **WHEN** N is sufficient (≥ 200)
- **THEN** the operation distribution includes:
  - `alloc_object` / `alloc_array` (~30% combined)
  - `field_set` / `array_elem_set` (~25% combined) — triggering
    barrier dispatches
  - `pin_root` / `unpin_root` (~25% combined; tracks live pin set)
  - `force_collect` (~5%)
  - `set_mode` (~1-2%, only in mode-switching test)
  - mix-in idle iterations (rest) — leave bookkeeping room

#### Scenario: Heap-ref writes trigger barrier path
- **WHEN** the workload performs `field_set` with a heap-ref value
- **THEN** `write_barrier_field` is invoked (assertions inside
  ArcMagrGC's existing barrier path) and the post-collect invariants
  still hold

### Requirement: All 3 GcMode variants stress-tested

#### Scenario: StwMarkSweep stress run passes
- **WHEN** `gc_stress_run_seeded(seed, iters, GcMode::StwMarkSweep)`
  completes
- **THEN** no panic, no invariant violation, and `force_collect` was
  invoked enough times to exercise the sweep path

#### Scenario: ConcurrentMarkSweep stress run passes
- **WHEN** `gc_stress_run_seeded(seed, iters, GcMode::ConcurrentMarkSweep)`
  completes
- **THEN** no panic; mark_queue is empty between collects

#### Scenario: GenerationalMarkSweep stress run passes
- **WHEN** `gc_stress_run_seeded(seed, iters, GcMode::GenerationalMarkSweep)`
  completes
- **THEN** no panic; young_list ⇔ gen_age invariant holds; promotion
  + tombstone bookkeeping correct

#### Scenario: Mode-switching stress run passes
- **WHEN** a stress run cycles through all 3 modes at random intervals
- **THEN** transitions between modes don't violate invariants
- **AND** in-flight bookkeeping (young_list, card_dirty, mark_queue)
  stays consistent across mode boundaries

### Requirement: Bounded test duration

#### Scenario: Default iters complete in reasonable time
- **GIVEN** `Z42_STRESS_ITERS` env var unset (default = 2000)
- **WHEN** all 4 stress tests run via `cargo test`
- **THEN** total wall time for the 4 stress tests is < 5 seconds on
  typical dev hardware (target: <1s per test in debug)

#### Scenario: Env var overrides iters
- **WHEN** `Z42_STRESS_ITERS=20000` is set
- **THEN** each stress test runs 20000 iterations
- **AND** any per-test seed limit-checks scale appropriately

### Requirement: Reproducibility guarantees

#### Scenario: Pinned seed produces predictable trace
- **WHEN** `gc_stress_run_seeded(42, 100, GcMode::StwMarkSweep)` is
  called and a future change to the test file is made
- **THEN** the same op sequence still runs (PRNG seed remains stable
  across rebuilds — `SmallRng::seed_from_u64` is reproducible)

## MODIFIED Requirements

### Requirement: arc_heap_tests module structure

**Before**: 16 test modules under `arc_heap_tests/`.

**After**: 17 — adds `stress` module.

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不变
- [x] VM interp — 不变
- [x] JIT — 不变
- [x] GC subsystem — only test code added
- [x] Tests — new `arc_heap_tests/stress.rs`

## IR Mapping

无新 IR 指令。
