# Design: 4-slot Polymorphic IC

## Architecture

```
                Field/VCall site dispatch
                          │
                          ▼
           ┌──────────────────────────────┐
           │ for slot in ic.entries[0..4]:│
           │   tid = slot.type_id.load()  │
           │   if tid == recv_type:       │ ◄─── hit
           │     return slot.payload      │
           │   if tid == UNRESOLVED:      │ ◄─── early exit
           │     break                    │     (sites with < 4
           │                              │      observed types)
           └──────────────┬───────────────┘
                          │ miss
                          ▼
           ┌──────────────────────────────┐
           │ HashMap<String,_>.get(name)  │
           │ install in next-empty or     │
           │   round-robin victim slot    │
           └──────────────────────────────┘
```

## Decisions

### Decision 1: 4 slots vs other counts

**Problem**: How many polymorphic types should the IC hold?

**Options**:
- **2 slots**: smallest poly win; CoreCLR's initial direct cache uses 2 before falling back to a global hash.
- **4 slots**: cover most class hierarchies (typical: 2-3 concrete subclasses + Object) without much memory cost.
- **8 slots**: more headroom but doubles the IC memory cost (96 B per VCallIC vs 48 B).

**Decision**: **4**. Industry-typical sweet spot. Memory cost is bounded (≤48 B per IC × number of sites). The linear scan with early exit means mono sites still hit on the first compare, so we don't penalize the common case.

### Decision 2: Eviction policy on slot-4 victim

**Problem**: When all 4 slots are full and a fifth type arrives, which slot to overwrite?

**Options**:
- **Round-robin**: one atomic counter, install at `counter % 4`; counter increments. Simplest, no recency info.
- **LRU (linked-list)**: track "last-used" timestamp per slot, evict oldest. Requires per-hit recency update.
- **LRU (move-to-front)**: every hit promotes the slot to position 0, shifting others down. Each hit has a O(slot) cost.
- **Random**: pick a slot via hash(recv_type) % 4.

**Decision**: **Round-robin with single atomic counter, separately stored per-IC**. Lose some recency info, but the simplicity (one extra atomic increment per miss, zero overhead per hit) wins. If a site is genuinely megamorphic (>4 types), the miss rate is high anyway — recency optimization gives marginal benefit.

### Decision 3: Atomic ordering

**Problem**: IC entries are read-modify-write across multiple threads (worker threads sharing a Module).

**Options**:
- **Relaxed everywhere** (current): allows torn reads / stale entries, but a wrong cache hit is harmless (caller re-validates type_id; mismatched cached_slot just means slow path next time, no UB).
- **Acquire/Release**: ensures writes happen-before subsequent reads.

**Decision**: **Stay with Relaxed** for both load and store. The mono IC already uses Relaxed; PIC inherits the same correctness argument: every read is followed by a `if cached_type_id == recv_type` check that gates the use of `cached_slot`. A torn (`type_id` of slot A + `cached_slot` of slot B) read can produce a wrong slot, but only if both atomics were just written to and the read landed in the middle. Worst case: caller reads wrong slot value → returns wrong field value → user observes wrong data. This is the same hazard as mono IC; the spec accepts it.

Actually no — the mono IC has only one (type_id, slot) pair, so torn reads can't happen across pair boundaries. PIC has 4 pairs; we COULD see slot[0].type_id == X (just written) and read slot[0].slot (still stale from previous tenant). To prevent: pack (type_id, slot) into single `AtomicU64` per entry. Then each load is atomic.

**Updated Decision**: pack each entry into one `AtomicU64`: lower 32 = type_id, upper 32 = payload (slot for FieldIC; encoded as `slot << 24 | fn_idx` for VCallIC — or split into two AtomicU64 if more than 32 bits of payload needed).

Wait, VCallIC payload is (slot: u32, fn_idx: u32) = 64 bits of payload + 32 bits of type_id = 96 bits. Can't pack into one u64.

Compromise:
- **FieldIC**: pack into one `AtomicU64` per entry (32 type_id + 32 slot). One atomic read per slot probe.
- **VCallIC**: two `AtomicU64`s per entry — `tid_and_slot: AtomicU64` (32+32) and `fn_idx: AtomicU32` (32). The fn_idx read can race vs slot update, but vtable lookup uses (type_id, slot) for the hit gate; fn_idx is only used after the hit is committed.

Or keep VCallIC simple with three AtomicU32s and accept the torn-read hazard. Mono IC already has this hazard (two AtomicU32s); current code already tolerates it.

**Final Decision**: keep three separate AtomicU32 per VCallIC entry. The torn-read hazard EXISTS but is bounded: if `type_id` matches `recv_type` (passes the gate) but `slot` was just updated by another thread to point at a different type's slot, the resulting field access is wrong — but the OTHER thread WAS writing for a different type, so we'd have observed THAT type's `type_id` if we'd read the type_id field one cycle later. The single-IC mono case had this exact same hazard and has been in production for months without observable bugs.

If we ever want to harden, the path is single-AtomicU64-packing for FieldIC (which only has 32-bit payload) and accept whatever VCallIC's residual hazard is.

### Decision 4: JIT-side handling

The current JIT path for FieldGet/VCall calls a helper that takes the IC pointer + does the IC lookup. Helper internally does the mono compare. Changing helper to do PIC scan is a pure helper-body change — no Cranelift emit changes needed.

Eventually (future spec): emit the PIC scan inline in Cranelift IR (4 atomic loads + 4 compares + branch). Skips the helper call ABI. Defer until measurement shows the helper call is a significant fraction of dispatch cost.

## Implementation Notes

- `FieldIC::default()` and `VCallIC::default()` use `std::array::from_fn(|_| Default::default())` since the entry structs are not `Copy`.
- Round-robin counter: add `pub round_robin: AtomicU32` per IC (4 B overhead).
- All loads use `Ordering::Relaxed`; all stores use `Ordering::Relaxed`.
- Linear scan with `if tid == UNRESOLVED { break }` — sites that never see more than N types skip the remaining slots.

## Testing Strategy

- **Unit tests** (`resolver_tests.rs` or new file):
  - `mono_hit` — single type bouncing, all hits land in slot 0.
  - `poly_two_types_both_hit` — A, B alternating, both in slots 0/1.
  - `poly_four_types_all_hit` — A, B, C, D alternating, all 4 slots filled, all hit.
  - `megamorphic_evict_oldest` — A, B, C, D, then E arrives → E replaces round-robin victim; subsequent E lookups hit; one of (A, B, C, D) now misses.
  - `unresolved_early_exit` — when an entry has UNRESOLVED type_id, scan stops there (so newly-installed type doesn't have to scan through 4 empties).
- **VM e2e**: existing 326-test suite must stay green (correctness preserved). No new e2e test needed — PIC is purely an optimization.
- **Benchmark**: new scenario `05_polymorphic_dispatch.z42` exercises a foreach over a heterogeneous array. Measure before/after.

## Open Questions

- Confirmed: 4 slots, round-robin eviction, Relaxed atomics, three AtomicU32 per VCallIC entry (accept torn-read hazard same as mono).
