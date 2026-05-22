# Design: Generational Mark-Sweep GC

## Architecture

```
                ┌─────────────────────────────────────────┐
                │  Region<ScriptObject>                   │
                │  ┌───────────────────────────────────┐  │
                │  │ chunks: Vec<Box<[E; 256]>>        │  │
                │  │ chunk0: E E E E E E E ... E       │  │
                │  │ chunk1: E E E E E E E ... E       │  │
                │  │ ...                               │  │
                │  └───────────────────────────────────┘  │
                │  young_list: Vec<(ci, ei)> ──┐          │
                │  card_dirty: Vec<u32>        │          │
                │  free_list:  Vec<(ci, ei)>   │          │
                └──────────────────────────────┼──────────┘
                                               │
                  ┌────────────────────────────┘
                  ▼
   RegionEntry {
     value: Mutex<T>,
     marked: AtomicU8,
     alive: AtomicBool,
     generation: AtomicU32,
     finalizer: Mutex<Option<FinalizerFn>>,
     location: (u16, u16),
     gen_age: AtomicU8,    ◀── NEW (P0)
   }

   Minor GC:                             Major GC:
   roots = pinned ∪ external             roots = pinned ∪ external
        ∪ iterate_dirty_cards            (no dirty card scan)
   mark BFS                              mark BFS (whole heap)
   sweep young_list only                 sweep_phase_full (all)
   promote survivors (gen_age++)         no promotion
   clear card_dirty                      clear card_dirty
```

## Decisions

### Decision 1: Logical promotion (gen_age field) over physical move

(See proposal "Design choice" section.) Physical move would invalidate
`GcRef::NonNull<RegionEntry>` (12B handle from `add-custom-allocator`).
Logical promotion preserves the contract: entry stays at its chunk
address forever; only its `gen_age` field changes.

Trade-off accepted: young+old entries interleave in chunks (cache
locality slightly worse than separated). Mitigation: iterate via
`young_list` direct indexing, not chunk linear scan.

### Decision 2: GcMode::GenerationalMarkSweep as independent variant

**Options**:
- A — independent variant: `GcMode { StwMarkSweep, ConcurrentMarkSweep, GenerationalMarkSweep }`
- B — orthogonal flag: keep ConcurrentMarkSweep, add separate bool
  `generational: bool`. Combos: stw+gen, stw+nogen, concurrent+gen,
  concurrent+nogen

**Decision**: A (independent variant, v1).

Rationale: 4 mode combos compound test burden. v1 ships generational
as one mode; combining with concurrent is a future spec
(`ConcurrentGenerational`) once both individual modes prove stable.

Each new mode is an enum variant + dispatch arm; doesn't bloat the
core code. Future combine: just add one more variant.

### Decision 3: Per-chunk card granularity (256 entries / card)

**Options**:
- Per-entry dirty bit (1 bit/entry): finest granularity; ~32 bytes/chunk
- Per-chunk dirty bit (1 bit/chunk): coarsest; ~1 bit/256 entries
- Mid: 16 entries/card (16 bits/chunk = 2 bytes/chunk): traditional
  card size matching cache lines

**Decision**: per-chunk dirty bit for v1. Storage cost is one `u32`
per chunk (over-allocated for alignment + future growth). Minor GC
scans dirty chunks → visits all 256 entries in those chunks looking
for old→young references.

Trade-off: more entries to scan per dirty chunk vs. simpler bitmap.
For typical workloads where 1-5% of chunks dirty per minor cycle,
this is fine. Future spec can switch to 16-entry cards if measured
benefit.

### Decision 4: gen_age uses AtomicU8

The write barrier reads `entry.gen_age` lock-free on every heap-ref
write. Atomic load + Relaxed ordering. Promotion writes happen during
sweep phase (STW), so no concurrent reader-writer race; but Atomic
avoids data race UB and matches mark bit pattern (existing precedent).

Cost: AtomicU8 = 1 byte. Total RegionEntry growth: +1 byte (+ possible
alignment padding). Negligible.

### Decision 5: Promotion threshold N = 2

**Options**:
- N=1: promote on first survival. Aggressive; quick to flush young.
- N=2: promote on second survival. Industry default (Java tenure
  threshold initial), filters most short-lived churn.
- N=3+: conservative; keeps young larger; more minor GCs.

**Decision**: N=2 for v1. Industry default; works for most workloads.
If measured needs (P3 bench shows promotion churn), the threshold can
be made configurable via env var (`Z42_GC_TENURE`).

### Decision 6: Minor GC trigger = young-list pressure

**When does minor GC fire automatically?**

- `maybe_auto_collect` currently triggers when total `used_bytes`
  approaches `max_bytes`. With generational, this would trigger major.
- Add: minor trigger when `young_list.len() * young_entry_size >
  young_capacity` (default young_capacity = 4 MB).
- Minor wins free space without major's full-heap pause.

Heuristic: in `maybe_auto_collect`, check young pressure first; if
young pressure trips → schedule minor; else if total pressure trips →
schedule major.

### Decision 7: Minor → major escalation heuristic

**Trigger**: after minor GC completes, if `young_list.len() / capacity
>= 0.75` (i.e., minor only freed 25% or less), immediately run major
in the same collect pass. Prevents minor-GC-thrashing on workloads
where promotion is high.

Configurable via `Z42_GC_MINOR_THRESHOLD` env var (default 0.75).

### Decision 8: Generational + concurrent are mutually exclusive (v1)

Combining tricolor incremental update with generational requires:
- Promotion happens during sweep (STW) → no race
- But barrier override must do BOTH: shade gray (for concurrent mark)
  AND mark card dirty (for generational minor GC)
- Two-purpose barrier doubles the work; need to validate correctness

v1 keeps them mutually exclusive. Future `ConcurrentGenerational` mode
in a follow-up spec.

### Decision 9: Major GC clears card_dirty

Major scans both gens fully → any cross-gen reference is found
naturally. Card_dirty is irrelevant during major; clear it at major's
end so next minor starts with fresh state.

Symmetric: minor clears card_dirty at its end too (after using cards
as additional roots).

### Decision 10: Tombstoned young entries clear from young_list

Sweep tombstones (alive=false, generation++, push slot to free_list).
Young entries also pop from young_list — otherwise next minor would
visit dead entries (alive=false check skips them but adds iteration
cost).

Done in `sweep_phase_young_only`: when an entry is unmarked + alive,
tombstone it AND remove its (ci, ei) from young_list.

## Implementation Notes

### RegionEntry layout (post-A3)

```rust
#[repr(C)]
pub struct RegionEntry<T> {
    pub(crate) value: Mutex<T>,                          // 8 B
    pub(crate) marked: AtomicU8,                         // 1 B
    pub(crate) alive: AtomicBool,                        // 1 B
    pub(crate) gen_age: AtomicU8,                        // 1 B  ◀── NEW
    pub(crate) generation: AtomicU32,                    // 4 B
    pub(crate) finalizer: Mutex<Option<FinalizerFn>>,    // ~16 B
    pub(crate) location: (u16, u16),                     // 4 B
}
// Total: ~36-40 B (+ T)
```

Adding gen_age: +1 byte; likely free with existing padding.

### Region layout (post-A3)

```rust
pub struct Region<T> {
    chunks: Vec<Box<[MaybeUninit<RegionEntry<T>>; 256]>>,
    next_bump: (u16, u16),
    free_list: Vec<(u16, u16)>,
    initialized: Vec<Vec<bool>>,
    young_list: Vec<(u16, u16)>,    // NEW: track young entries
    card_dirty: Vec<u32>,           // NEW: per-chunk dirty bit
    // ...
}
```

### `Region<T>::alloc` (modified)

```rust
pub fn alloc(&mut self, value: T) -> RegionHandle {
    let (chunk_idx, entry_idx, generation) = /* existing logic */;
    // gen_age defaults to 0 in RegionEntry::new
    self.young_list.push((chunk_idx, entry_idx));
    RegionHandle { chunk_idx, entry_idx, generation }
}
```

### `Region<T>::promote(handle)` (new)

```rust
pub fn promote(&mut self, handle: RegionHandle) -> bool {
    let entry = self.resolve(handle);
    let new_age = entry.gen_age.fetch_add(1, Ordering::AcqRel) + 1;
    if new_age >= PROMOTION_THRESHOLD {
        // Remove from young_list (linear search OK; called only at
        // sweep, not on hot path)
        if let Some(pos) = self.young_list.iter().position(|&p|
            p == (handle.chunk_idx, handle.entry_idx))
        {
            self.young_list.swap_remove(pos);
        }
        true
    } else {
        false
    }
}
```

### Write barrier override under GenerationalMarkSweep

```rust
fn write_barrier_field(&self, owner: &Value, slot: usize, new: &Value) {
    if self.mode() == GcMode::GenerationalMarkSweep {
        // Cross-gen check: owner old, new young?
        let owner_age = match owner {
            Value::Object(gc) => gc.entry_ptr().gen_age.load(Ordering::Relaxed),
            Value::Array(gc)  => gc.entry_ptr().gen_age.load(Ordering::Relaxed),
            _ => return,
        };
        let new_age = match new {
            Value::Object(gc) => gc.entry_ptr().gen_age.load(Ordering::Relaxed),
            Value::Array(gc)  => gc.entry_ptr().gen_age.load(Ordering::Relaxed),
            _ => return,
        };
        if owner_age >= 1 && new_age == 0 {
            // Mark owner's chunk dirty
            // (Determine which region: object vs array; mark
            // appropriate region's card)
            self.mark_card_for_owner(owner);
        }
    }
}
```

### Minor GC loop sketch

```rust
fn collect_minor(&self) -> u64 {
    // STW pause (existing safepoint protocol)
    let _pause = request_gc_pause(ctx);

    // Roots = pinned + external + dirty-card entries
    let mut roots = self.snapshot_pinned_roots();
    roots.extend(self.dirty_card_entries(&self.region_object));
    roots.extend(self.dirty_card_entries(&self.region_array));

    // Mark BFS (existing logic, but only visits young + reachable old)
    self.mark_from_roots(&roots);

    // Sweep young_list only
    let freed = self.sweep_phase_young_only();

    // Promote survivors
    self.promote_young_survivors();

    // Clear card_dirty for next cycle
    self.region_object.lock().card_dirty.fill(0);
    self.region_array.lock().card_dirty.fill(0);

    freed
}
```

### Major GC loop

```rust
fn collect_major(&self) -> u64 {
    // STW pause (existing logic)
    let _pause = request_gc_pause(ctx);

    // Mark BFS (whole heap)
    self.mark_phase();

    // Full sweep (existing sweep_phase, walks both regions)
    let freed = self.sweep_phase_full();

    // Clear card_dirty (next minor starts fresh)
    self.region_object.lock().card_dirty.fill(0);
    self.region_array.lock().card_dirty.fill(0);

    freed
}
```

## Testing Strategy

### Unit tests (region_tests.rs + new generational module)

- `alloc_pushes_to_young_list` (sanity)
- `promote_increments_gen_age` (N steps)
- `promote_at_threshold_removes_from_young_list`
- `tombstone_removes_from_young_list`
- `mark_card_dirty_sets_bit_at_chunk_offset`
- `iterate_dirty_cards_yields_all_entries_in_dirty_chunks`
- `dirty_cards_cleared_after_minor`

### ArcMagrGC scenarios (new arc_heap_tests/generational.rs)

- `minor_gc_only_scans_young_list_not_all_entries`
- `minor_gc_promotes_after_n_survivals` (n=1, n=2, n=3 boundary)
- `major_gc_unaffected_by_gen_age`
- `cross_gen_write_marks_card_minor_sees_target`
  (without card marking, the target would be incorrectly swept)
- `intra_gen_writes_do_not_mark_cards`
- `minor_to_major_escalation_when_young_dense`
- `gen_age_bookkeeping_inert_under_StwMarkSweep_mode` (parity check)

### End-to-end

- `Z42_GC_MODE=generational ./scripts/test-stdlib.sh` 72/72 GREEN
- `test-all.sh --scope=full` GREEN under all 3 modes (stw, concurrent,
  generational)
- Existing cross_thread_smoke.rs tests pass unchanged (generational
  transparent under StwMarkSweep mode default)

### Bench (P3)

- `gc_minor/pure_churn` — 100k alloc + die (no surviving old gen);
  measures young-only collection
- `gc_minor/mixed_workload` — 10k old + 100k young churn; measures
  card marking + young scan + escalation heuristics
- `gc_major/large_old_gen` — old-heavy workload, periodic major GC
- Compare against `Z42_GC_MODE=stw` baseline (no minor; every collect
  is full)

Expected: 10–50× improvement on minor-only pause time on
short-lived-heavy workloads. Major GC pause unchanged.

## Phasing

5 phases, each independently committable + GREEN:

- **P0**: RegionEntry gen_age field + Region young_list + card bitmap
  fields + iterate_young + mark_card_dirty + clear_card_dirty + promote
  helpers. Standalone unit tests. No callers yet.
- **P1**: Add `GcMode::GenerationalMarkSweep` variant. Default mode
  bookkeeping (alloc pushes to young_list); barrier override checks
  gen_age. Behavior parity check: existing tests GREEN under STW mode
  even with bookkeeping enabled.
- **P2**: Minor GC dispatch (sweep_phase_young_only + promote_survivors +
  card marking active). Tests for minor-only, promotion, cross-gen
  writes.
- **P3**: Major GC dispatch + escalation heuristic + auto-collect
  young-pressure trigger. Full integration with safepoint /
  `collect_cycles_with_context`.
- **P4**: Bench + docs + archive. Generational variants for the
  4 existing gc_cycle_bench workloads + new gc_minor/* workloads.
  vm-architecture.md sync.

## Deferred / Future Work

### add-concurrent-generational
- Concurrent mark + generational. Barrier override does both shading
  (for concurrent mark) AND card marking (for generational). Combines
  the two latest GC specs. Major engineering; do after generational
  proves stable.

### add-multi-generation
- 3+ generations (young, intermediate, old). Reduces premature promotion
  for workloads with bimodal lifetime. Future perf spec.

### add-adaptive-promotion
- Adjust promotion threshold N based on heap dynamics (slow + dense →
  higher N; fast + sparse → lower N). Adaptive heuristic.

### add-per-thread-young-arena
- Per-VmContext local young region for alloc fast path. Periodic
  batch flush to shared young at minor GC. Reduces young alloc lock
  contention.

### add-physical-promotion
- Move entries between separate young + old regions (requires
  GcRef rewriting). Better cache locality. Engineering challenge.

### add-card-granularity-tuning
- Sub-chunk card sizes (e.g., 16 entries/card). Reduces minor GC
  rescan cost for partial-dirty chunks. Future perf spec.
