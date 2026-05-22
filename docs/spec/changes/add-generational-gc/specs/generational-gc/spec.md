# Spec: Generational Mark-Sweep GC

## ADDED Requirements

### Requirement: gen_age field tracks per-entry generation

#### Scenario: Fresh alloc starts at gen_age = 0
- **WHEN** `Region<T>::alloc(value)` is called
- **THEN** the returned `RegionEntry<T>` has `gen_age == 0`
- **AND** the handle is pushed to the region's `young_list`

#### Scenario: gen_age is atomic-readable
- **WHEN** a write barrier accesses `entry.gen_age`
- **THEN** the read is a lock-free atomic load (`Relaxed` ordering
  sufficient — gen_age changes are mediated by STW handshakes at
  promotion time)

### Requirement: young_list provides O(young) iteration

#### Scenario: Newly-alloc'd entry appears in young_list
- **WHEN** `Region::alloc` returns a fresh handle
- **THEN** `iterate_young(visit)` calls `visit` on the new entry

#### Scenario: Promoted entry is removed from young_list
- **WHEN** `Region::promote(handle)` is called and brings gen_age to
  the promotion threshold (default = 2)
- **THEN** the handle is removed from `young_list`
- **AND** subsequent `iterate_young` does NOT visit the entry

#### Scenario: Tombstoned young entry is removed
- **WHEN** sweep tombstones a young entry (unmarked + alive=false)
- **THEN** the handle is removed from `young_list` (next minor GC
  doesn't re-visit dead young slots)

### Requirement: Card marking tracks cross-gen writes

#### Scenario: Old → young heap write marks the card dirty
- **WHEN** the write barrier observes a write where the OWNER's
  `gen_age >= 1` (promoted/old) AND the NEW value's underlying
  RegionEntry has `gen_age == 0` (young)
- **THEN** the owner's chunk in the region's `card_dirty` bitmap is
  marked dirty
- **AND** subsequent minor GC adds entries from dirty chunks as
  additional roots

#### Scenario: Young → young write does NOT mark a card
- **WHEN** owner's gen_age == 0 AND new's gen_age == 0
- **THEN** no card is dirtied (both already in young scan; redundant)

#### Scenario: Old → old write does NOT mark a card
- **WHEN** owner's gen_age >= 1 AND new's gen_age >= 1
- **THEN** no card dirtied (intra-old writes don't escape to young)

#### Scenario: Primitive write does NOT mark a card
- **WHEN** new is a primitive (not heap-ref)
- **THEN** no card dirtied (already filtered at call site per
  `add-write-barriers` Decision 1)

#### Scenario: Cards reset after major GC
- **WHEN** a major GC completes (full scan of both generations)
- **THEN** all `card_dirty` bits are cleared (next minor starts fresh)

### Requirement: Promotion after N minor survivals

#### Scenario: Entry survives N=2 minor GCs → promoted
- **GIVEN** N (promotion threshold) = 2
- **WHEN** an entry's gen_age == 0 and survives a minor GC
- **THEN** gen_age becomes 1
- **AND** entry remains in young_list (one more survival needed)
- **WHEN** the same entry's gen_age == 1 and survives another minor GC
- **THEN** gen_age becomes 2
- **AND** entry is REMOVED from young_list (promoted to old)

#### Scenario: Major GC does not touch gen_age
- **WHEN** a major GC runs
- **THEN** gen_age values are unchanged (major reclaims unmarked
  entries regardless of generation; surviving entries keep their
  current gen_age)

### Requirement: Minor GC scans young + dirty cards only

#### Scenario: Minor GC marks young + dirty-card entries
- **WHEN** minor GC mark phase runs
- **THEN** roots include: (a) pinned roots, (b) external_root_scanner,
  (c) entries in dirty chunks of the region (via
  `iterate_dirty_cards`)
- **AND** BFS marks only entries reachable from those roots
- **AND** old-gen entries NOT in dirty chunks are skipped entirely

#### Scenario: Minor sweep tombstones unmarked young entries only
- **WHEN** minor GC sweep phase runs
- **THEN** for each entry in `young_list`: if unmarked, tombstone +
  fire finalizer + remove from young_list
- **AND** old-gen entries are NOT visited (sweep only walks young_list)
- **AND** card_dirty bitmap is cleared (next minor starts fresh)

### Requirement: Major GC escalation

#### Scenario: Major GC scans both young + old
- **WHEN** major GC is triggered (either explicit `force_collect` or
  escalated from minor)
- **THEN** mark phase walks all alive entries in both regions
  (current `mark_phase` BFS behavior, unchanged)
- **AND** sweep walks both young_list AND `iterate_alive` (catches
  promoted-but-unreachable entries)
- **AND** card_dirty is cleared

#### Scenario: Minor escalates to major when young still dense after minor
- **GIVEN** a heuristic threshold (default: 75% of young_list survives
  minor — i.e., promotion + survival rate is high)
- **WHEN** minor GC completes with high survival rate
- **THEN** the heap escalates immediately to a major GC in the same
  collect pass (single STW window)

### Requirement: GcMode::GenerationalMarkSweep

#### Scenario: Mode switch activates generational dispatch
- **WHEN** `heap.set_mode(GcMode::GenerationalMarkSweep)` or
  `Z42_GC_MODE=generational`
- **THEN** subsequent `collect_cycles_with_context` calls dispatch to
  the minor/major path
- **AND** write barriers under this mode check cross-gen + mark cards
- **AND** alloc paths push to young_list

#### Scenario: Default mode unchanged
- **WHEN** mode is `StwMarkSweep` (default) or `ConcurrentMarkSweep`
- **THEN** generational logic is INERT — alloc still pushes to
  young_list (free bookkeeping) but minor GC path is never taken;
  full collect behaves identically to pre-spec
- **AND** card_dirty bitmap is never read

#### Scenario: Generational and concurrent are mutually exclusive (v1)
- **WHEN** mode is set to `GenerationalMarkSweep`
- **THEN** concurrent-mark-sweep semantics are not active (no
  concurrent mark thread, no tricolor barrier shading)
- **AND** all GC pauses are STW (minor + major)
- **AND** a future `ConcurrentGenerational` mode (separate spec) would
  combine both

### Requirement: Behavior parity with StwMarkSweep when generational inert

#### Scenario: Existing tests pass with generational bookkeeping enabled but mode StwMarkSweep
- **WHEN** mode is `StwMarkSweep` (default)
- **AND** generational fields (gen_age, young_list, card_dirty) exist
  but are never consulted
- **THEN** all existing arc_heap_tests pass byte-identical to pre-spec
- **AND** stdlib `test-all.sh --scope=full` 6 stages GREEN
- **AND** alloc_throughput bench numbers within ±5% of pre-spec
  (any deviation must be justified by extra alloc bookkeeping cost)

## MODIFIED Requirements

### Requirement: alloc tracks young entries

**Before** (`add-custom-allocator`): `Region::alloc` allocates into a
chunk slot, returns handle. No generational bookkeeping.

**After**: `Region::alloc` also pushes the (chunk_idx, entry_idx) to
`young_list`. Cost: one Vec push per alloc (~5–10 ns amortized).

### Requirement: sweep behavior depends on collect tier

**Before**: `sweep_phase` walks both region_object + region_array,
tombstones unmarked entries. Single sweep code path.

**After**: split into `sweep_phase_young_only` (called by minor GC,
walks `young_list` only) and `sweep_phase_full` (called by major GC,
walks all entries via `iterate_alive`). Original `sweep_phase` becomes
`sweep_phase_full` for backward compat with StwMarkSweep mode.

### Requirement: barrier override branches on GcMode

**Before**: Override checks `mode == ConcurrentMarkSweep` for tricolor
shading; otherwise no-op.

**After**: Override checks mode:
- `StwMarkSweep` → no-op
- `ConcurrentMarkSweep` → tricolor shading (unchanged)
- `GenerationalMarkSweep` → if `owner.gen_age >= 1 && new.gen_age == 0`,
  mark card dirty for owner's chunk

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不变
- [x] VM interp — 不变（barrier 走相同 trait dispatch）
- [x] JIT — 不变
- [x] GC subsystem — 主要变更（region 加 gen_age + young_list + card; ArcMagrGC 分 minor/major）
- [x] stdlib — 不变（Std.GC.Finalize 等 API 全部 generational-transparent）

## IR Mapping

无新 IR 指令。
