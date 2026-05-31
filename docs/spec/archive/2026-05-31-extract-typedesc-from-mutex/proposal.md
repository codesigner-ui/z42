# Proposal: extract `ScriptObject.type_desc` out of the Mutex

## Why

Every read of `obj.type_desc.<anything>` in z42 currently goes through
`parking_lot::Mutex::lock()` because `type_desc` lives inside the
per-entry mutex protecting `ScriptObject`. The lock is uncontended on
the single-mutator hot path, but it still costs ~5–10 ns per read
(atomic CAS + memory barrier) on x86_64 / aarch64.

This blocks every JIT inline opportunity that needs the receiver's
type identity:

- **VCall PIC fast path** — the helper must lock the mutex just to read
  `type_desc.id` for the 4-slot IC scan. Inlining the scan into JIT is
  currently impossible because re-implementing `parking_lot` Mutex
  acquisition in Cranelift IR is impractical (cross-platform
  futex/syscall fallback path).
- **IsInstance / AsCast** — same issue; the type check pays a lock per
  call.
- **FieldGet / FieldSet PIC fast path** — same issue (already inlined
  in the interp dispatch, JIT still goes through the helper).

`ScriptObject.type_desc` is write-once at construction (`alloc_object`)
and never mutated for the object's lifetime (verified: 0 mutation sites
in the runtime — `grep -rn 'borrow_mut.*type_desc\|type_desc *=' src/`).
The Mutex is therefore over-protection. Moving the field to an unlocked
header makes type-id reads three native loads (one for the pointer to
the object, one for the type_desc Arc, one for the id field) — no lock
at all.

CoreCLR (`MethodTable*` in `ObjHeader`) and HotSpot (`klass*` in
`oopDesc`) both put type identity in a fixed offset at the start of
every heap object, outside any synchronization scope. This proposal
aligns z42 with that established pattern.

## What Changes

- Split `ScriptObject` into a `ScriptObjectHeader` (unlocked,
  immutable) holding `type_desc` and `type_args`, plus a
  `ScriptObjectBody` (Mutex-protected) holding `slots` and `native`.
- Update `RegionEntry<ScriptObject>` layout so the header sits outside
  `Mutex<ScriptObjectBody>`.
- Add `GcRef::type_desc(&self) -> &Arc<TypeDesc>` and
  `GcRef::type_args(&self) -> &[String]` for the lockless reads.
- Migrate every `obj.borrow().type_desc` / `.type_args` call site
  (~19 reads) to the new accessors.
- Update GC mark traversal so the mark-and-sweep collector still walks
  the type_desc reference (it's a heap Arc; mark phase needs to see
  it). The traversal already runs unlocked relative to the entry
  mutex; only the field's storage location changes.
- Update the docs/design/runtime/gc.md object-layout diagram.

## Scope (允许改动的文件)

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `src/runtime/src/metadata/types.rs` | MODIFY | Split `ScriptObject` into `ScriptObjectHeader` + `ScriptObjectBody`; keep public API on a thin `ScriptObject` newtype facade |
| `src/runtime/src/gc/region.rs` | MODIFY | `RegionEntry<ScriptObject>` carries header outside Mutex; sweep / mark traversal updated |
| `src/runtime/src/gc/refs.rs` | MODIFY | Add `type_desc()` / `type_args()` accessors on `GcRef<ScriptObject>` |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | `alloc_object` writes the new split layout; mark traversal reads type_desc unlocked |
| `src/runtime/src/interp/exec_object.rs` | MODIFY | Migrate 4–5 read sites to new accessors |
| `src/runtime/src/interp/exec_vcall.rs` | MODIFY | Migrate 2 read sites; PIC slow path unchanged |
| `src/runtime/src/interp/dispatch.rs` | MODIFY | Migrate 1 read site |
| `src/runtime/src/jit/helpers/object.rs` | MODIFY | Migrate FieldGet / FieldSet / IsInstance / AsCast helpers to unlocked reads |
| `src/runtime/src/jit/helpers/vcall.rs` | MODIFY | jit_vcall PIC scan now reads type_id without locking |
| `src/runtime/src/jit/helpers/value.rs` | MODIFY | ToStr / Equals fallbacks (1 read site) |
| `src/runtime/src/corelib/object.rs` | MODIFY | Construction sites (`weak_handle_type_desc`, builtin object dispatch) |
| `src/runtime/src/exception/mod.rs` | MODIFY | Exception construction uses new layout |
| `docs/design/runtime/vm-architecture.md` | MODIFY | Update Phase-2 IC fast-path note; new "object layout" sub-section |
| `docs/design/runtime/gc.md` | MODIFY | Update ObjHeader / Region layout diagram + invariant note |

**只读引用**：

- `src/runtime/src/gc/arc_heap_tests/*.rs` — verify GC tests still pass against new layout (no source change, only invocation sites)
- `docs/design/runtime/concurrency.md` — confirm the new lockless read aligns with the documented concurrency model

## Out of Scope

- Slots / native data layout — they stay inside the Mutex (writeable, must stay synchronized).
- type_desc mutation API — the spec assumes write-once-at-alloc; if a
  future feature ever needs to swap type_desc post-construction
  (object pinning across hot-reload, e.g.), that's a separate spec
  that introduces `ArcSwap` or a dedicated rebind helper. The current
  cross-zpkg fixup pass mutates `TypeDesc` itself (via `Arc::get_mut`),
  not the per-instance pointer; it's unaffected.
- PIC inline emission in Cranelift — covered by a follow-up spec
  (`jit-pic-inline`) that can land cleanly once this prerequisite
  ships.
- StringId / per-instance type_args migration — review.md E2.P1 step 2
  work; orthogonal.

## Open Questions

- [ ] Should `type_args` (also write-once) move to the header alongside `type_desc`? — Recommended yes, since both share the same read pattern and same write-once invariant. Confirmation needed.
- [ ] Does the concurrent-GC stale-mark-race investigation (`docs/spec/changes/investigate-concurrent-gc-stale-mark-race/`) interact with the mark-traversal change here? — Need to coordinate before implementation begins.
