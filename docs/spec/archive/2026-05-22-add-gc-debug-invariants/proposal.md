# Proposal: GC Debug-Only Invariant Checks

## Why

Four GC algorithms landed in rapid succession (A1 / A2 / A3 / A4):
custom allocator, mark-sweep, generational, concurrent mark. Each
introduced multiple data-structure invariants (young_list ⇔ gen_age
consistency, free_list ⇔ alive=false, card_dirty range, mark_queue
emptiness, generation monotonic, etc.). Currently these are only
**implicit** in code — no runtime checks catch a regression.

A debug-only `validate_gc_invariants` helper runs after every
collect cycle in debug builds, asserting that the heap is in a
consistent state. Release builds skip these checks entirely (no
production overhead). Future GC changes that accidentally violate
an invariant fail fast at the next test run instead of degrading
silently into use-after-free or heap corruption.

This is the **safety net** for the heavy-weight GC investment of the
past sessions. Low cost, high value.

## What Changes

- New `ArcMagrGC::debug_validate_invariants()` method, `cfg(debug_assertions)`-gated
- Region-level checks (in `Region<T>`):
  - Every entry in `young_list` has `gen_age < PROMOTION_THRESHOLD`
  - Every entry with `gen_age < PROMOTION_THRESHOLD` and `alive == true`
    is in `young_list` (exactly once)
  - Every slot in `free_list` has `alive == false`
  - Every entry has `location == (chunk_idx, entry_idx)` matching
    its actual chunk position
  - `card_dirty.len() == chunks.len()` (one bitmap entry per chunk)
- ArcMagrGC-level checks:
  - `mark_queue` empty outside of an active concurrent mark
  - No `alive=true` entry with `marked=1` after sweep completes
  - `stats.used_bytes` lower-bound check (≤ sum of estimated entry sizes)
- Integration: `collect_cycles` (all paths) calls
  `debug_validate_invariants` at the end. `arc_heap_tests` mod's test
  helpers also call it on common entry points so regressions surface
  immediately.
- Tests that intentionally violate invariants (to demonstrate the
  check fires) are placed in `arc_heap_tests::invariants` —
  `should_panic` style tests for each invariant.

**Default behavior unchanged**: release builds compile the entire
validation path out. Debug builds (and `cargo test`) get the safety
net. No new public API.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/region.rs` | MODIFY | Add `debug_validate_invariants(&self)` impl on `Region<T>`, returning structured violations or panicking via `debug_assert!`. cfg(debug_assertions). |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | Add `debug_validate_invariants(&self)` on `ArcMagrGC` — calls both regions + own heap-wide checks. Invoke at end of `collect_cycles` and `collect_cycles_with_context`. cfg(debug_assertions). |
| `src/runtime/src/gc/arc_heap_tests/invariants.rs` | NEW | Tests that explicitly check `debug_validate_invariants` catches violations — temporarily corrupt heap state then assert panic. |
| `src/runtime/src/gc/arc_heap_tests/mod.rs` | MODIFY | Register `invariants` module. |
| `docs/design/runtime/gc.md` | MODIFY | New "Debug invariants" subsection documenting what's checked, when, and how to add new invariants. |

**只读引用**：region.rs / arc_heap.rs (existing fields).

## Out of Scope

- **Production-mode invariant checks**: this spec stays debug-only.
  Release builds get no checks. Future spec (`add-gc-runtime-checks`)
  could opt-in via feature flag for selected production checks if a
  user wants paranoid mode.
- **Cross-thread race detection**: invariants assume single-thread or
  STW state. Concurrent mode mid-cycle checks are out of scope.
- **Property-based stress tests** (C2): separate spec — generates
  random workloads + applies invariants. This spec just provides the
  checker primitive.
- **Performance profiling of validation cost**: debug builds aren't
  perf-critical. If `cargo test` slows noticeably, can throttle (e.g.
  validate every Nth collect).

## Open Questions

- Should violations panic immediately (`debug_assert!`) or return
  `Result<(), Violation>` for tests to inspect? → **Both**: provide
  `validate(&self) -> Result<(), Violation>` returning structured
  errors; `debug_validate_invariants(&self)` wrapper calls
  `validate().unwrap_or_else(|v| panic!("invariant violation: {}", v))`.
  Tests use the Result form; production integration uses the panicking
  form.
