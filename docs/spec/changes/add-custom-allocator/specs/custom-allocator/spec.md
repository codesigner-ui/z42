# Spec: Custom Region Allocator

## ADDED Requirements

### Requirement: Region<T> chunked storage

#### Scenario: alloc into empty region creates first chunk
- **WHEN** `Region<T>::alloc(value)` is called on a freshly-constructed region
- **THEN** a chunk is allocated (Boxed; `CHUNK_SIZE` entries pre-reserved
  but uninitialized)
- **AND** the value is written into entry index 0 of chunk 0
- **AND** the returned `(chunk_idx=0, entry_idx=0)` handle is stable
  for the lifetime of the chunk

#### Scenario: alloc beyond chunk capacity grows
- **WHEN** the current chunk is full and `alloc` is called
- **THEN** a new chunk is allocated and pushed
- **AND** subsequent allocs land in the new chunk (bump pointer reset
  within new chunk)
- **AND** previously-returned handles into older chunks remain valid
  (chunks are never moved or freed wholesale)

#### Scenario: sweep recycles entry slots into a free list
- **WHEN** sweep tombstones an entry (sets `alive = false`)
- **THEN** the (chunk_idx, entry_idx) is pushed into the region's
  free list
- **AND** subsequent `alloc` calls pop from free list first (before
  growing the bump pointer)

### Requirement: GcRef<T> as handle, not Arc

#### Scenario: GcRef::clone is integer copy, no atomic op
- **WHEN** `GcRef::clone(&ref)` is called
- **THEN** no atomic operation occurs
- **AND** the resulting `GcRef` references the same `RegionEntry` as
  the source
- **AND** profiling shows no `fetch_add` on the clone path

#### Scenario: GcRef::drop is no-op
- **WHEN** a `GcRef<T>` goes out of scope (Rust-level drop)
- **THEN** no refcount decrement occurs
- **AND** no finalizer fires (finalizer fires only at sweep)
- **AND** the underlying `RegionEntry` is unchanged until GC sweep
  marks it unreachable

#### Scenario: GcRef::as_ptr returns stable address
- **WHEN** `GcRef::as_ptr(&ref)` is called
- **THEN** the returned `*const Mutex<T>` points into the `RegionEntry`'s
  inner Mutex
- **AND** the address remains stable for the lifetime of the entry
  (until tombstoned by sweep)
- **AND** identity hashing in corelib (`__obj_get_hash_code`,
  `__obj_eq`) continues to work unchanged

#### Scenario: GcRef::borrow / borrow_mut deref through region
- **WHEN** `ref.borrow()` is called
- **THEN** the region is consulted to resolve `(chunk_idx, entry_idx)`
  to a `&RegionEntry<T>`
- **AND** the entry's `Mutex<T>` is locked (parking_lot blocking lock,
  same semantics as today)
- **AND** the returned guard borrows the inner value

### Requirement: WeakGcRef tombstone semantics

#### Scenario: WeakGcRef survives normal scope exit of GcRef
- **WHEN** all strong `GcRef`s to an entry drop (Rust scope exit)
- **AND** the entry has not yet been swept
- **THEN** `WeakGcRef::upgrade()` returns `Some(GcRef)` — entry is still
  reachable until GC declares it dead
- **AND** the WeakGcRef remains valid

#### Scenario: WeakGcRef invalidates after sweep tombstones the entry
- **WHEN** sweep tombstones an entry (no longer reachable from roots)
- **THEN** `WeakGcRef::upgrade()` returns `None`
- **AND** subsequent allocs may reuse the slot — the new entry has a
  bumped generation counter, so the stale WeakGcRef's generation no
  longer matches and `upgrade` continues to return `None`

#### Scenario: Generation counter prevents ABA
- **GIVEN** a WeakGcRef pointing at `(chunk_idx, entry_idx, gen=5)`
- **WHEN** the entry is swept (gen→6) and the slot reused by a fresh
  alloc (entry now has gen=6 and a different value)
- **THEN** the stale WeakGcRef sees gen mismatch → `upgrade` returns `None`
- **AND** no spurious "upgrade succeeded with wrong object" occurs

### Requirement: Finalizer fires at sweep, not at drop

#### Scenario: Finalizer fires when sweep frees an unreachable object
- **WHEN** an object with a registered finalizer becomes unreachable
- **AND** `collect_cycles` (or `force_collect`) runs
- **THEN** the finalizer is invoked exactly once during sweep
- **AND** the order of finalizer firings within a single sweep is
  unspecified (matches current cycle-collector contract)

#### Scenario: Finalizer does NOT fire on Rust scope exit
- **WHEN** a `GcRef` falls out of Rust scope (and no other GcRef refers
  to the entry)
- **AND** GC has not run yet
- **THEN** the finalizer is NOT invoked at scope exit
- **AND** the entry remains in the region (potentially observable via
  `iterate_live_objects`) until next sweep

#### Scenario: Std.GC.Finalize(x) forces immediate finalization
- **WHEN** z42 code calls `Std.GC.Finalize(handle)` on an object with
  a registered finalizer
- **THEN** the finalizer is invoked synchronously
- **AND** the entry is tombstoned (no longer reachable via the handle's
  strong reference)
- **AND** the slot enters the region's free list (reused by next alloc)
- **AND** any other strong references to the same entry become
  observably "tombstoned" — accessing their data may surface as a
  documented error (Decision pending; see design.md)

### Requirement: Mark/sweep walks regions, not heap_registry

#### Scenario: Sweep iterates region.entries linearly
- **WHEN** `sweep_phase` runs
- **THEN** it walks `region_object.entries` + `region_array.entries`
  in order (no `Weak::upgrade()` per entry)
- **AND** for each entry: if `marked == 0` and `alive == true`,
  invoke finalizer (if any) + set `alive = false` + bump generation +
  push slot index to free list
- **AND** for each entry: if `marked == 1`, reset to 0 (prep next cycle)
- **AND** profiling shows the per-entry overhead drops from 1 atomic
  load (Weak::upgrade) to 1 byte read (alive flag)

#### Scenario: heap_registry field removed
- **WHEN** `ArcMagrGC` struct is inspected post-spec
- **THEN** no `heap_registry: Vec<WeakRef>` field exists
- **AND** all callers that previously walked the registry now walk
  regions directly

### Requirement: Std.GC.Finalize builtin

#### Scenario: Std.GC.Finalize(x) z42-script entry point
- **GIVEN** a z42 script holding `var f = Std.IO.OpenFile("/tmp/x")`
- **WHEN** the script calls `Std.GC.Finalize(f)`
- **THEN** the file is closed immediately (finalizer fires)
- **AND** subsequent uses of `f` document the tombstoned state
  (Decision: throw `Std.ObjectDisposedException` or return Null;
  pinned in design.md)

#### Scenario: Calling Finalize on object without finalizer is no-op
- **WHEN** `Std.GC.Finalize(plainObject)` runs on an object with no
  registered finalizer (most objects)
- **THEN** no error is thrown
- **AND** the object's strong refs continue to work; sweep will free
  it normally when unreachable

### Requirement: Default GcMode behavior preserved

#### Scenario: STW mark-sweep collect frees unrooted cycle (parity)
- **WHEN** `GcMode::StwMarkSweep` (default) is active
- **AND** an unrooted cycle exists
- **AND** `collect_cycles` runs
- **THEN** both cycle nodes are freed (their finalizers fire if registered)
- **AND** `freed_bytes` is non-zero
- **AND** behavior matches pre-spec STW path

#### Scenario: ConcurrentMarkSweep collect with region backing
- **WHEN** `GcMode::ConcurrentMarkSweep` is active
- **AND** `collect_cycles_with_context(&ctx)` runs
- **THEN** the 6-phase concurrent flow (snapshot → yield → drain →
  handshake → sweep) works with region-backed entries
- **AND** the barrier-shaded path (mark + push to mark_queue) operates
  on `RegionEntry.marked` directly
- **AND** all existing `arc_heap_tests::concurrent_mark` scenarios stay GREEN

## MODIFIED Requirements

### Requirement: Finalizer-firing timing contract

**Before** (current): Finalizer fires at the moment the last `Arc`
strong reference drops. Cyclic finalizers fire during cycle collector
break + alive_vec drop.

**After**: Finalizer fires during `sweep_phase` when the region entry
is identified as unreachable. Two firing routes:
1. **Automatic**: GC sweep finds the entry unmarked → finalizer fires.
2. **Manual**: User calls `Std.GC.Finalize(x)` → finalizer fires
   immediately, slot tombstoned.

The automatic-on-drop path is removed entirely.

### Requirement: heap_registry → region

**Before**: `ArcMagrGC` had `heap_registry: Vec<WeakRef>`; the
authoritative liveness store + iteration target for snapshot /
iterate_live_objects / sweep. Pruning via `Weak::upgrade() == None`.

**After**: `ArcMagrGC` has `region_object: Region<ScriptObject>` +
`region_array: Region<Vec<Value>>`. These are the authoritative store.
Iteration walks them directly. Liveness = `entry.alive == true`. The
WeakRef-based pruning model is gone.

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不变（GcRef API surface preserved）
- [x] VM interp — 不变（barrier dispatch unchanged）
- [x] JIT — 不变（jit_field_set / jit_array_set unchanged）
- [x] GC subsystem — 主要变更（region allocator + handle refs + sweep walks regions）
- [x] stdlib — 新增 `Std.GC.Finalize(x)` API + migrate any RAII finalizer users

## IR Mapping

无新 IR 指令。
