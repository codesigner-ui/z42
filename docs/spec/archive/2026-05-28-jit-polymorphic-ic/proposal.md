# Proposal: 4-slot Polymorphic IC for FieldIC + VCallIC (review.md C4 P2 + C5 P2)

## Why

`FieldIC` and `VCallIC` are currently **monomorphic** — they cache exactly one `(type_id, slot)` pair. When a site sees a second receiver type, the cache is overwritten, and bouncing between 2+ types keeps invalidating the cache, falling back to `HashMap<String, usize>` lookup every time.

Typical polymorphic sites:
- Heterogeneous collection iteration: `foreach (var x in mixedList) x.GetType().Name`
- Generic stdlib code that handles `Object` / `Array` / `Str` polymorphically
- Virtual dispatch over a class hierarchy (3-4 concrete classes)

review.md C4 P2 + C5 P2 both call for 4-slot PIC. Pure runtime change; no wire format / IR bump.

## What Changes

- `FieldIC`: replace `(cached_type_id, cached_slot)` with `[FieldICEntry; 4]` where each entry is `(type_id, slot)`. Linear scan with early exit on first `UNRESOLVED` slot.
- `VCallIC`: same pattern, `[VCallICEntry; 4]` each is `(type_id, slot, fn_idx)`.
- `exec_object::field_get/set` IC lookup: scan all 4 slots; on miss, install in first empty slot (or victimize slot 3 with simple round-robin if all filled).
- `exec_vcall::vcall` similar update.
- JIT helpers (`hr_field_get`, `hr_field_set`, `hr_vcall`) read updated IC layout. Cranelift-side already calls helper for IC miss — no change needed there.

## Scope

| File | Type | Description |
|------|------|-------------|
| `src/runtime/src/metadata/resolver.rs` | MODIFY | Redefine `FieldIC` / `VCallIC` to 4-slot arrays; update `Default` impl |
| `src/runtime/src/interp/exec_object.rs` | MODIFY | `field_get` / `field_set` PIC lookup + install |
| `src/runtime/src/interp/exec_vcall.rs` | MODIFY | `vcall` PIC lookup + install (mirror exec_object) |
| `src/runtime/src/jit/helpers/object.rs` | MODIFY | `jit_field_get` / `jit_field_set` PIC lookup |
| `src/runtime/src/jit/helpers/vcall.rs` | MODIFY | `jit_vcall` PIC lookup |
| `src/runtime/src/metadata/resolver_tests.rs` | NEW (or MODIFY existing) | Unit tests: mono hit, poly add, megamorphic victim |
| `bench/scenarios/05_polymorphic_dispatch.z42` | NEW | E2E bench: heterogeneous list with mixed types |
| `docs/design/runtime/vm-architecture.md` | MODIFY | Update IC section to describe PIC |

**Only-read references**:
- `src/runtime/src/metadata/tokens.rs` — `TypeId::UNRESOLVED` sentinel
- `src/runtime/src/interp/exec_instr.rs` — caller for field_get/set, no change

## Out of Scope

- **Name interning** (field/method `String` → `NameId`): C4 P1 + C5 P1, separate spec. The PIC win is achievable without it — IC miss still goes through the existing `HashMap<String>` lookup, just less often.
- **IR opcode wire format change** (`FieldGet { field_name: String }` → `field_name_id: u32`): C4 P3, separate spec needs zbc bump.
- **Megamorphic global table** (CoreCLR-style `ResolveCacheElem` hash with chained collision): the 4-slot LRU drops 5+ types back to per-site HashMap; a global hash is more work than the win justifies at z42's scale.
- **JIT inline PIC scan** (emit the 4-slot linear scan inline in Cranelift IR instead of a helper call): defer until we measure that the helper call dominates the PIC lookup cost. Currently the helper call is ~one-time-per-IC-miss after warmup.

## Open Questions

- [ ] Eviction policy when all 4 slots are full: round-robin counter (simplest) vs LRU (one extra atomic per hit)? Round-robin loses recency info but is cheaper; LRU preserves locality but each hit must update a position.
- [ ] Are 4 slots enough for typical z42 workloads? CoreCLR uses 2 directly (then a global cap-2048 hash). Java HotSpot uses up to 8 in some configurations. 4 is a reasonable midpoint; we can revisit after bench.

## References

- review.md C4 (`Field offset: HashMap 慢路径 ⚠️ IC 已做、底层未优`)
- review.md C5 (`虚调用 dispatch（VCall）：3 层 fallback 链 ⚠️`)
- CoreCLR `ResolveCacheElem` (`vm/virtualcallstub.h:59-81`)
- Existing impl: `src/runtime/src/metadata/resolver.rs` (mono IC)
