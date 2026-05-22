# Proposal: GC Random-Workload Stress Test

## Why

`add-gc-debug-invariants` (just landed) gives us 8 invariants enforced
post-collect. Coverage so far: each invariant has a focused unit test
+ existing GC tests don't violate any invariant. **But fixed test
fixtures only exercise the obvious paths.** A subtle bug — e.g. a
specific alloc/promote/tombstone ordering across mode switches — is
exactly what random workloads catch.

This spec adds a deterministic, seedable random-workload generator
that drives a `ArcMagrGC` through thousands of mixed operations
(alloc, write, pin, unpin, force_collect, set_mode) under all 3
GcMode variants. After each operation, invariants are checked via
the C1 validator. A panic surfaces the seed so the failure is
reproducible. This is the **dynamic counterpart** to the static
unit tests + integration runs.

Builds directly on C1's `Region<T>::validate` + `ArcMagrGC::
debug_validate_invariants` primitives — no new validator code needed.
Spec scope is **just the workload driver + integration**, not new
invariants.

## What Changes

- New `gc/arc_heap_tests/stress.rs` module:
  - `gc_stress_run_seeded(seed: u64, iters: usize, mode: GcMode)`
    function: drives `iters` random operations under `mode`,
    validating after each one
- Operations covered:
  - `alloc_object` / `alloc_array` (random size, fields, type)
  - `set_field` / `set_array_elem` (random index, random value type
    including heap refs → triggers barrier)
  - `pin_root` / `unpin_root` (track per-test pin handles)
  - `force_collect` (random frequency, ~1/20 ops)
  - `set_mode` (~1/200 ops, cycles through stw/concurrent/generational)
- Reproducibility: seeded by `u64` parameter; on failure the test
  panics with the seed in the message so reruns are deterministic
- 3 default seed-set tests for each mode:
  - `stress_seeded_stw_short`
  - `stress_seeded_concurrent_short`
  - `stress_seeded_generational_short`
  - `stress_seeded_mode_switching_short` (one long run cycling through
    all 3 modes)
- Each runs ~2000 iterations under cargo test (small enough to be
  fast; expand via env var `Z42_STRESS_ITERS` for longer local runs)

**Default behavior unchanged**: stress tests are `#[test]`-gated, run
only under `cargo test`. Release builds compile out the validator
(per C1) so even if stress generators were somehow active in release,
they'd be normal workloads — no production hook.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/arc_heap_tests/stress.rs` | NEW | Seeded random workload generator + per-mode tests |
| `src/runtime/src/gc/arc_heap_tests/mod.rs` | MODIFY | Register `stress` module |
| `docs/design/runtime/gc.md` | MODIFY | New "Stress testing" subsection right after "Debug invariants"; Phase table row + C2 backlog entry "future" → "landed" |

**只读引用**：existing C1 validator primitives.

## Out of Scope

- **Multi-thread stress**: `cross_thread_smoke.rs` already covers
  thread-crossing patterns. C2 is single-thread random ops.
- **`proptest` / `quickcheck` integration**: hand-rolled SmallRng + u64
  seed gives reproducibility without new dev-dependency.
- **Continuous fuzzing in CI**: this spec runs ~2k iters in `cargo test`.
  Long-form fuzz (millions of iters) is a future ops spec if needed.
- **Coverage-guided fuzzing** (e.g. cargo-fuzz with libFuzzer):
  requires `#[no_mangle]` harness setup; future spec.
- **Performance metrics during stress**: spec just validates
  invariants; pause-time/throughput measurement is benchmark work,
  not stress.

## Open Questions

无 — 设计直接展开在 design.md。
