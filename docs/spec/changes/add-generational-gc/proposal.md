# Proposal: Generational Mark-Sweep GC

## Why

Current GC (`add-mark-sweep-collector` + `add-custom-allocator`) is a
**single-generation** mark-sweep: every `collect_cycles` scans every
reachable object across the entire heap. Pause time scales as
O(reachable) — for a workload with N long-lived objects + M short-lived
churn, every collect pays the full N+M cost even when most short-lived
work just died.

The **generational hypothesis** (universally validated across Java,
.NET, V8, Ruby, etc.): most objects die young, a small fraction lives
long. Splitting the heap into young + old generations + collecting
young frequently (O(young) pause) + old infrequently delivers the
canonical pause-time / throughput improvement.

z42's existing infrastructure makes A3 a **bounded structural addition**
rather than a rewrite:

- ✅ `Region<T>` chunked storage + stable `NonNull` pointers
  (`add-custom-allocator`)
- ✅ Write barrier call-site wiring at every `FieldSet` / `ArraySet`
  (`add-write-barriers`)
- ✅ `GcMode` runtime switch + safepoint protocol
  (`add-concurrent-gc`)
- ✅ Tricolor mark + sweep skeleton in `ArcMagrGC` (regions already
  separate per T-type)

A3 adds:

1. **`gen_age: u8` on `RegionEntry`** (1 byte/entry) — 0 = young,
   1+ = promoted on Nth survival
2. **`young_list: Vec<(u16,u16)>`** on `Region<T>` — O(young) iteration
   for minor GC, not O(total)
3. **Card marking** via per-chunk dirty bit — write barrier overrides
   record `old.slot = young_ref` writes so minor GC re-roots from
   dirty cards in old gen
4. **Minor / Major collect dispatch** — minor scans young_list + dirty
   cards; major escalates to current full-heap behavior
5. **Promotion** — entries surviving N=2 minor GCs get `gen_age++` and
   removed from young_list (logical promotion; physical address
   stays — preserves `NonNull` contract from `add-custom-allocator`)

## Design choice: logical promotion (gen_age field)

**Logical promotion** (entry stays at same chunk address, just bumps
`gen_age` field) is chosen over physical promotion (move entry to
separate old-region chunks). Rationale:

- `GcRef<T>` is `NonNull<RegionEntry<T>>` (12B handle). Physical move
  would invalidate every existing GcRef pointing at the moved entry —
  requires either pointer rewriting (complex, error-prone) or handle
  indirection (negates `add-custom-allocator`'s zero-indirection win)
- Cache locality cost of mixed young+old in same chunks is bounded:
  iterate uses `young_list` direct indexing, not chunk linear scan
- Memory cost: 1 byte per entry; with CHUNK_SIZE=256 entries, padding
  considerations are negligible
- Simpler is better — physical-move generational requires sub-spec for
  pointer rewriting algorithm; not worth it for a v1

## What Changes

- `RegionEntry<T>`: new field `gen_age: AtomicU8` (atomic so concurrent
  mode's barrier can read it lockfree)
- `Region<T>`: new field `young_list: Vec<(u16, u16)>` tracking
  chunk_idx+entry_idx of all young entries; `card_dirty: Vec<u32>`
  (one u32 per chunk = bitmap with chunk position) for card marking
- `Region<T>::alloc` pushes new entries to `young_list` (default
  gen_age=0)
- `Region<T>::promote(handle)` increments gen_age, removes from
  young_list once threshold reached
- `Region<T>::mark_card_dirty(chunk_idx)` for write barrier
- `Region<T>::iterate_young(visit)` walks young_list (O(young))
- `Region<T>::iterate_dirty_cards(visit)` walks dirty chunk bitmap +
  yields all entries in those chunks
- New `GcMode::GenerationalMarkSweep` variant — orthogonal to
  `StwMarkSweep` / `ConcurrentMarkSweep`. Activates the minor/major
  dispatch path
- `ArcMagrGC` write_barrier_field / write_barrier_array_elem override:
  under `GenerationalMarkSweep`, if `owner.gen_age >= 1 && new.gen_age == 0`,
  call `region.mark_card_dirty(owner.chunk_idx)`
- `ArcMagrGC::collect_cycles_with_context` adds minor/major dispatch:
  minor GC scans young_list + dirty cards (re-rooting any old→young
  reference); promotes survivors; major escalates to existing full
  collect_cycles_stw path when (a) young_list empty after minor or
  (b) heap pressure trips old-gen threshold
- Promotion policy: N=2 (survive 2 minor GCs → promote). Conservative;
  reduces premature-promotion churn
- Tests for: alloc-into-young, promote-after-N-minors, minor-clears-
  young-only, dirty-card-rescan, escalation-to-major-on-young-overflow
- Bench: `gc_minor/*` workloads measuring minor pause vs full
  collect (existing P3 bench)
- Docs: `vm-architecture.md` "GC heap backing" subsection extends with
  "Generational layout"; new "A3 generational" Phase row; A3 entry
  "future" → "landed"

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/region.rs` | MODIFY | Add gen_age + young_list + card bitmap fields; alloc bookkeeping; promote() + mark_card_dirty() + iterate_young() / iterate_dirty_cards() helpers |
| `src/runtime/src/gc/region_tests.rs` | MODIFY | New tests: alloc pushes to young_list; promote increments gen_age + removes from list; iterate_young O(young); card dirty + clear |
| `src/runtime/src/gc/refs.rs` | MODIFY | `GcRef::gen_age()` accessor exposing the entry's gen_age (read-only, atomic load); used by barrier override |
| `src/runtime/src/gc/mode.rs` | MODIFY | Add `GcMode::GenerationalMarkSweep` variant + env-var parsing (`Z42_GC_MODE=generational`) |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | Minor / major GC dispatch under generational mode; barrier override for cross-gen detection; promotion logic in minor sweep |
| `src/runtime/src/gc/arc_heap_tests/generational.rs` | NEW | Spec-scenario tests: minor scans only young; major scans all; cross-gen writes mark cards; promotion after N=2; escalation heuristic |
| `src/runtime/src/gc/arc_heap_tests/mod.rs` | MODIFY | Register `generational` module |
| `src/runtime/benches/gc_cycle_bench.rs` | MODIFY | Add `gc_minor/*` workloads: large old-gen + small young-gen alloc → minor pause measurement vs full collect baseline |
| `docs/design/runtime/vm-architecture.md` | MODIFY | Generational layout subsection; Phase table + A3 entry; promotion + card marking docs; trigger criteria |

**只读引用**（理解上下文必须读，但不修改）：

- `src/runtime/src/gc/safepoint.rs` — phase + pause guard (no change; generational uses same STW pause path for minor)
- `src/runtime/src/gc/heap.rs` — MagrGC trait (signature unchanged; only override changes)
- `src/runtime/src/interp/exec_object.rs` / `exec_array.rs` — barrier call sites (no change; same trait dispatch)
- `src/runtime/src/jit/helpers/object.rs` / `array.rs` — same

## Out of Scope

- **Concurrent minor GC**: minor stays STW in v1. Concurrent young
  collection has its own design challenges (incremental promotion,
  young scan racing with mutator alloc). Follow-up perf spec if measured.
- **Multi-generation (3+ gens)**: stays at young + old binary
  generation. Adding more generations is a future perf spec.
- **Physical promotion / semi-space**: keep logical promotion; physical
  move requires GcRef rewriting (out of scope per design).
- **Per-thread young arenas**: shared young region per heap in v1.
  Per-VmContext local young arenas would reduce alloc lock contention
  but adds promotion sync complexity.
- **Generational + concurrent combined**: `GcMode::GenerationalMarkSweep`
  does STW minor + STW major. Future mode `GcMode::ConcurrentGenerational`
  combines with concurrent-mark — separate spec.
- **Card marking granularity tuning**: per-chunk bit (256 entries per
  card) is the v1 default. Sub-chunk granularity is future perf work.
- **Generation count adjustment by workload**: v1 fixed at N=2.
  Adaptive promotion threshold is future perf work.
- **Tenure age inheritance through cycle**: if A points to B and both
  promote, they promote independently. No "generation propagation".

## Open Questions

无新的 — 所有 spec-level decision 在 design.md "Decisions" 段展开：

- Promotion threshold N=2（设计 D5）
- Card marking 粒度 = chunk-level（256 entries/card，设计 D3）
- Generational 是独立 GcMode 还是 orthogonal flag（设计 D2 → 独立 mode v1）
- Minor 触发条件（young alloc pressure，设计 D6）
- Major 触发条件（minor 后 young_list 仍密集 + old pressure，设计 D7）
- 已有的 ConcurrentMarkSweep 是否在 generational 下也可用 → 设计 D8（不，v1 互斥）
