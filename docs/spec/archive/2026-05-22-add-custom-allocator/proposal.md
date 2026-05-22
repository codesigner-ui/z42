# Proposal: Custom Region Allocator (replace Arc backing)

## Why

`GcRef<T>` currently uses `Arc<GcAllocation<T>>` as the storage handle.
This is the "Phase 1 接口收口、行为零变化" formulation from
`vm-architecture.md` — explicitly documented as a transitional shape.
Two years of GC iterations (mark-sweep, write barriers, concurrent
mark, multi-collector arbitration) have stabilized the API + safepoint
protocol, and the Arc-tracing hybrid now blocks the next round of GC
work:

1. **Per-allocation overhead**: ~48-64 bytes wrapper (Arc header + 2
   Mutex + AtomicU8) + 1 malloc per object. For object-heavy
   workloads (closures, arrays, instances), this is real cost: every
   `alloc_object` is a malloc + Arc init; every `GcRef::clone()` is an
   atomic fetch_add.
2. **Hybrid semantics confusion**: Arc refcount + mark-sweep tracing
   coexist; "when does Drop fire" depends on cycle reachability. Spec
   contracts (finalizer-at-drop) leak the Arc abstraction.
3. **Blocks generational GC (A3)**: A3 needs young-gen / old-gen
   regions for promotion. Arc-backed objects scatter across `malloc`'s
   address space — incompatible with region-based promotion.
4. **Blocks MMTk integration (D1)**: MMTk binding requires a
   region-style allocator interface. Arc backing can't be the host of
   that interface.

This spec replaces Arc backing with a chunked region allocator + handle
indirection. `GcRef<T>` becomes `(region_id, entry_index)`. Pointer
stability for `as_ptr()` is preserved via chunked Box-owned storage
(chunks never relocate). Mark-sweep semantics become **pure tracing**:
- Reachable from roots = live
- Unreachable = dead, freed at sweep, finalizer fired at sweep
- No more "Arc refcount drops to 0 → immediate finalizer fire"

The finalizer-timing change is the spec's biggest behavioral break.
We accept it because:
- z42 is pre-1.0; migration cost is finite (small stdlib + user surface)
- `philosophy.md` "不为旧版本提供兼容" applies — we can break cleanly
- Pure-tracing semantics matches every production GC (Java, Go, .NET
  Server GC, V8); RAII-via-finalizer was never a correct contract
- Mitigation: new `Std.GC.Finalize(x)` API lets users explicitly fire
  the finalizer for prompt resource cleanup. Pattern: `Close()` /
  `Dispose()` method on resource types, called explicitly (matches
  Java `AutoCloseable`, .NET `IDisposable`)

## What Changes

- New `Region<T>` (chunked storage, stable entry addresses) +
  `RegionEntry<T>` (per-object slot with marked bit, alive tombstone,
  inline finalizer slot, Mutex<T> for concurrent access)
- `GcRef<T>` becomes a `(region_handle, entry_index)` value type;
  `Clone` is integer copy, no atomic op
- `WeakGcRef<T>` becomes the same handle + generation count for
  tombstone-based invalidation (replaces `std::Weak`)
- `ArcMagrGC` holds per-T-kind regions: `Region<ScriptObject>`,
  `Region<Vec<Value>>` (the two heap-resident Value variants)
- Mark phase: BFS sets `marked` directly on `RegionEntry`; same API
- Sweep phase: walks regions linearly (no more `heap_registry: Vec<Weak>`
  + `Weak::upgrade()` per object), frees unmarked entries (tombstones
  them + invokes their finalizer + recycles slot into free list)
- New `Std.GC.Finalize(x)` builtin — fires `x`'s finalizer immediately
  + tombstones the slot (for RAII migration paths)
- Migrate the ~10 tests in `gc/arc_heap_tests/finalization.rs` and 1-2
  in corelib that assert "Drop triggers finalizer" to the new contract
- Document in `vm-architecture.md`: "GC heap backing" chapter rewrite
  + finalizer contract update

**Crucially preserved**: `GcRef::as_ptr()` / `ptr_eq()` semantics
(identity hashing in corelib stays correct), `MagrGC` trait surface
(no signature changes), `GcMode` selection (STW + concurrent both
keep working), all spec scenarios from `add-mark-sweep-collector` /
`add-write-barriers` / `add-concurrent-gc`.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/region.rs` | NEW | `Region<T>` chunked allocator + `RegionEntry<T>` |
| `src/runtime/src/gc/refs.rs` | MODIFY | `GcRef<T>` / `WeakGcRef<T>` redefined as handle types; remove `Arc` import; reimplement all methods (`new` becomes "alloc into region"; `borrow` / `borrow_mut` deref through region; `mark` / `is_marked` on RegionEntry) |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | `ArcMagrGC` gains `region_object: Region<ScriptObject>`, `region_array: Region<Vec<Value>>`; `alloc_object` / `alloc_array` allocate into regions; `mark_phase` / `sweep_phase` rewritten to walk regions; `heap_registry: Vec<WeakRef>` deleted (regions are the authoritative liveness store) |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | Drop semantics: `GcRef::drop` becomes no-op (no refcount); region entries freed by sweep. Finalizer dispatch moved to `sweep_phase` (already partially there; consolidate) |
| `src/runtime/src/gc/mod.rs` | MODIFY | Export new `Region` types if public, otherwise stays |
| `src/runtime/src/metadata/types.rs` | MODIFY | `Value::Object(GcRef<ScriptObject>)` / `Value::Array(GcRef<Vec<Value>>)` unchanged at the type level — but the backing storage shape changes (transparent to consumers) |
| `src/runtime/src/corelib/gc.rs` | MODIFY | Add `Std.GC.Finalize(x)` builtin; register in builtin map |
| `src/libraries/z42.core/src/Std/GC.z42` | MODIFY | Add `Finalize(x: Object): void` user-facing API |
| `src/runtime/src/gc/arc_heap_tests/finalization.rs` | MODIFY | Migrate ~10 "drop → finalizer fires" assertions to "force_collect → finalizer fires" or "Std.GC.Finalize(x) → fires immediately" |
| `src/runtime/src/gc/arc_heap_tests/region_alloc.rs` | NEW | New tests for region allocator: bump pointer advances, free list reuses slots, chunk overflow grows, pointer stability across allocs |
| `src/runtime/src/gc/arc_heap_tests/weak_refs.rs` | MODIFY | Tests that exercise `Weak::upgrade()` after drop now exercise tombstone-after-sweep |
| `src/runtime/src/gc/arc_heap_tests/mod.rs` | MODIFY | Register new `region_alloc` module |
| `src/runtime/src/corelib/process.rs` | MODIFY | `builtin_process_handle_drop` switches from "auto on Drop" to "explicit `Close()` API" OR keep finalizer + document that close happens at GC sweep (Decision in design.md) |
| `src/runtime/benches/gc_cycle_bench.rs` | MODIFY | Add `alloc_throughput_*` workloads measuring alloc/sec; compare pre-spec (Arc) vs post-spec (region) |
| `docs/design/runtime/vm-architecture.md` | MODIFY | "GC heap backing" chapter rewrite: Arc → Region; finalizer contract update; A1 entry moves "future" → "landed"; Phase table grows |

**只读引用**（理解上下文必须读，但不修改）：

- `src/runtime/src/gc/safepoint.rs` — phase + pause guard semantics (no change; allocator-agnostic)
- `src/runtime/src/gc/mode.rs` — GcMode (no change; orthogonal)
- `src/runtime/src/metadata/types.rs::trace_children` (no change; iterates Value)
- `src/runtime/src/interp/exec_object.rs` / `exec_array.rs` — FieldSet / ArraySet (no change; barrier dispatch unchanged)
- `src/runtime/src/jit/helpers/object.rs` / `array.rs` — same

## Out of Scope

- **Generational GC (A3)**: separate spec; depends on this. Young-gen
  promotion uses per-generation regions but isn't this spec's scope.
- **Per-VmContext local arenas**: this spec uses single global regions
  per type per heap. Per-thread arenas (work-stealing alloc) are a
  perf spec if measured contention.
- **Object-size classes / size-bucketed regions**: single region per
  T-type. Future perf spec can introduce small/medium/large size
  classes if heap fragmentation becomes a measured concern.
- **MMTk integration (D1)**: this spec gives MMTk the binding shape
  it needs but doesn't actually integrate MMTk.
- **Concurrent sweep**: sweep stays STW (same as
  `add-concurrent-gc`). Concurrent sweep + region recycling is its
  own follow-up.
- **Pointer compression / handle encoding**: handles stay as
  `(u32, u32)` pairs in v1. Compaction (single u64) is a perf
  optimization if measured.

## Open Questions

无新的 — 所有 spec-level decision 在 design.md "Decisions" 段展开：

- Finalizer firing: sweep-only + explicit `Std.GC.Finalize(x)` API
- WeakRef invalidation: generation-counter tombstone
- Pointer stability: chunked storage, never relocate entries
- Concurrent access: Mutex<T> stays on RegionEntry (preserves current
  concurrency model from add-multithreading-foundation)
- 资源类型迁移路径: stdlib RAII patterns get explicit `Close()` /
  `Dispose()` methods + spec scenario documents this
