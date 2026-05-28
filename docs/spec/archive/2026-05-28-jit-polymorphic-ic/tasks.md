# Tasks: 4-slot Polymorphic IC

> Status: 🟢 已完成
> Created: 2026-05-28
> Completed: 2026-05-28

## Phase 1: data structures (resolver.rs)

- [x] 1.1 Define `FieldICEntry { type_id: AtomicU32, slot: AtomicU32 }`
- [x] 1.2 Define `VCallICEntry { type_id: AtomicU32, slot: AtomicU32, fn_idx: AtomicU32 }`
- [x] 1.3 Redefine `FieldIC` as `{ entries: [FieldICEntry; 4], round_robin: AtomicU32 }`
- [x] 1.4 Redefine `VCallIC` as `{ entries: [VCallICEntry; 4], round_robin: AtomicU32 }`
- [x] 1.5 Update `Default` impl for both via `std::array::from_fn`

## Phase 2: PIC lookup in interp

- [x] 2.1 `exec_object::field_get` — replace mono compare with 4-slot linear scan (with UNRESOLVED early exit). On miss, install via `field_ic_install`.
- [x] 2.2 `exec_object::field_set` — same.
- [x] 2.3 `exec_vcall::vcall` — same pattern for VCallIC.
- [x] 2.4 Helper functions `field_ic_lookup(ic, type_id) -> Option<u32>`, `field_ic_install(ic, type_id, slot)`, `vcall_ic_lookup(ic, type_id) -> Option<(u32, u32)>`, `vcall_ic_install(ic, type_id, slot, fn_idx)` — shared between interp + JIT helpers via `metadata::resolver`.

## Phase 3: PIC lookup in JIT helpers

- [x] 3.1 `jit_field_get` — uses `field_ic_lookup` + `field_ic_install` via `&*ic_ptr`.
- [x] 3.2 `jit_field_set` — same.
- [x] 3.3 `jit_vcall` — uses `vcall_ic_lookup` + `vcall_ic_install`.

## Phase 4: tests

- [x] 4.1 Unit tests in `metadata::resolver_tests` (12 new tests):
  - `field_ic_mono_hit`
  - `field_ic_poly_two_types_both_hit`
  - `field_ic_poly_four_types_all_hit`
  - `field_ic_megamorphic_evicts_via_round_robin`
  - `field_ic_unresolved_recv_type_returns_none`
  - `field_ic_install_unresolved_is_noop`
  - `field_ic_default_all_slots_unresolved`
  - `vcall_ic_mono_hit`
  - `vcall_ic_poly_four_types`
  - `vcall_ic_default_all_slots_unresolved`
  - `ic_reinstall_same_type_updates_slot_in_place`
- [x] 4.2 GREEN verification: 693/693 lib + 326/326 VM e2e (interp + JIT) + 2/2 cross-zpkg.

## Phase 5: benchmark

- [x] 5.1 New scenario `bench/scenarios/05_polymorphic_dispatch.z42` — 4-way polymorphic loop (Cat/Dog/Cow/Pig). Interp 3354 ms, JIT 2186 ms (1.53× JIT speedup). Total 25M output verified.

## Phase 6: docs

- [x] 6.1 Update `docs/design/runtime/vm-architecture.md` IC section with PIC linear scan + round-robin eviction protocol.
- [x] 6.2 Mark review.md C4 P2 + C5 P2 ✅ in priority tables.
- [x] 6.3 Archive spec when GREEN — `docs/spec/archive/2026-05-28-jit-polymorphic-ic/`.

## Notes

- Pure runtime change — no zbc / zpkg wire format bump.
- Mono fast path preserved: site that sees one type hits on slot 0 in 1 compare (same as before).
- Memory: FieldIC 8 B → 36 B (4 × 8 B + 4 B round-robin). VCallIC 12 B → 52 B.
- Per-function IC table size grows ~4-5× for FieldGet/Set/VCall sites. For a function with ~20 such sites, that's ~700 B extra. Acceptable.
- Pre-existing failures (unrelated to PIC; confirmed on baseline):
  - `corelib::process::process_tests::slot_ids_are_monotonic_unique` + `corelib::crypto::crypto_tests::returns_requested_length` — flaky under parallel test execution, pass single-threaded.
  - `Z42NetWebSocketServerTests.*` — `Std.Time.Stopwatch.Stop` register-Null issue, exists on baseline, unrelated to FieldIC.
  - `IncrementalBuildIntegrationTests.StdlibBuild_SecondRun_AllCached` — pre-existing, exists on baseline.

## References

- proposal.md
- design.md
- review.md C4 P2 + C5 P2
