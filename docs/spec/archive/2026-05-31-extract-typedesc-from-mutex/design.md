# Design: extract `ScriptObject.type_desc` out of the Mutex

## Architecture

### Current layout

```
RegionEntry<ScriptObject> {            ┐
    value: Mutex<ScriptObject> {       │
        type_desc: Arc<TypeDesc>,      │  ← all reads need lock
        slots:     Vec<Value>,         │
        native:    NativeData,         │
        type_args: Box<[String]>,      │
    },                                 │
    marked, alive, gen_age,            │
    generation, finalizer, ...         │
}                                      ┘
```

### Target layout

```
RegionEntry<ScriptObject> {
    header: ScriptObjectHeader {                       ← lockless reads
        type_desc: Arc<TypeDesc>,
        type_args: Box<[String]>,
    },
    body: Mutex<ScriptObjectBody> {                    ← still locked
        slots:  Vec<Value>,
        native: NativeData,
    },
    marked, alive, gen_age,
    generation, finalizer, ...
}
```

`ScriptObject` becomes a thin newtype wrapper exposing the same public
API; internally it owns one header + one body. Module-private code
constructs and walks them directly; downstream users keep using the
existing `Value::Object(GcRef<ScriptObject>)` and `obj.borrow()` /
`obj.borrow_mut()` API for the body.

The mark-and-sweep traversal walks the header's `type_desc` and the
body's `slots` separately; the type_desc walk no longer needs the body
lock.

## Decisions

### Decision 1: keep `Mutex<ScriptObjectBody>` (don't go fully lockless)

**问题**: slots / native are mutable (every FieldSet writes a slot;
StringBuilder mutation goes through native). Whether to keep the Mutex
or move to lockless atomics.

**选项**:
- A — Keep `Mutex<ScriptObjectBody>` as today.
- B — Switch slots to `Vec<AtomicU64>` (Value is 24 B so atomic store
  would need tearing or per-slot lock).
- C — Per-slot RwLock.

**决定**: A. The concurrency model documented in
`docs/design/runtime/concurrency.md` already prescribes the per-entry
Mutex; this proposal narrows the locked region to exactly the mutable
parts. B / C would change a load-bearing design contract for marginal
gain and exposes the GC mark-update sequence to tearing.

### Decision 2: type_args lives in the header

**问题**: type_args is write-once-at-alloc-time and read-only afterwards
(verified via grep — only `obj.type_args = ...` site is in `obj_new`).
Same invariant as type_desc.

**选项**:
- A — Move type_args to the header.
- B — Leave type_args inside the body Mutex.

**决定**: A. Identical write-once invariant; identical access cost
benefit; treating them differently would introduce an asymmetry that
future readers would have to remember.

### Decision 3: GC mark traversal reads type_desc unlocked

**问题**: mark-and-sweep needs to follow the `type_desc` reference so
the TypeDesc Arc itself stays live. The collector currently does
`entry.value.lock().type_desc.clone()` inside the mark phase.

**选项**:
- A — Keep the mark phase locking (defeats the perf benefit for the
  collector but mutator hot path still wins).
- B — Mark phase reads type_desc unlocked from the header; only locks
  body when walking slots.

**决定**: B. Header is immutable post-construction; reading it without
the lock is sound. Collector writes (the mark bit) target
`RegionEntry.marked`, not the header — no race.

### Decision 4: `GcRef::type_desc()` returns `&Arc<TypeDesc>`, not `Arc<TypeDesc>`

**问题**: Returning `Arc` clones bumps the refcount on every JIT IC
check — defeats the perf goal.

**选项**:
- A — Return `&Arc<TypeDesc>` borrowed for the GcRef's lifetime.
- B — Return `Arc<TypeDesc>` (cloned).
- C — Return `&TypeDesc` (one level deref'd).

**决定**: C. The borrow of the inner `TypeDesc` is what callers
actually want (they read `.id`, `.name`, `.vtable_index`); returning
`&TypeDesc` is one fewer indirection at the call site. Lifetime is
tied to the underlying `RegionEntry`, which is stable for the entry's
generation. Internal Arc still keeps the TypeDesc alive across GC
cycles via the mark-traversal.

### Decision 5: thread safety of header reads

**问题**: parallel mutators reading a header field — is a memory
barrier needed?

**Header fields are write-once at allocation, then never mutated.** The
allocation happens before the `GcRef` is published to other threads
(the `alloc_object` return value is the first reference). Any thread
that reaches a `GcRef` has a synchronized-with relationship via the
allocation handshake (the heap's allocation Mutex). No additional
fencing required — same semantics as immutable shared state in Rust.

## Implementation Notes

### Migration helper

Provide a transitional `ScriptObject::type_desc(&self) -> &TypeDesc` so
existing `obj.borrow().type_desc.xxx` call sites can be rewritten as
`obj.type_desc().xxx` without touching the borrow API yet — keeps the
PR small and reviewable. Then in a follow-up rename, drop the redundant
`.borrow()` from the unlocked-read sites and route everything through
`GcRef::type_desc(rc)`.

### Type-args migration

Mirror the same accessor pattern: `GcRef::type_args(rc) -> &[String]`
and `ScriptObject::type_args(&self) -> &[String]` for the legacy path.

### GC barrier surface

`write_barrier_field` currently only fires on slot writes, which still
go through `borrow_mut()`. Header writes happen exactly once at
`alloc_object` and the barrier already handles initial-write via the
allocation path. No barrier changes needed.

### JIT helper updates

Once the migration is done, `jit_vcall` can be edited to read
`type_id` via the new accessor without locking. The PIC scan itself
(4-slot linear lookup) doesn't change — only the type-id load preceding
it.

Per the proposal's out-of-scope clause, fully inlining the IC scan into
Cranelift IR is a follow-up; this spec just makes it possible.

## Testing Strategy

- **Unit tests**: existing `arc_heap_tests` + `region` tests must pass
  unchanged — they exercise the GC traversal and lifecycle, which now
  has a slightly different layout but identical semantics.
- **GC stress test**: `concurrent_gc_mode_stress_no_race_no_leak`
  (currently being investigated separately) must continue to behave —
  the header change moves the type_desc Arc out of the locked region
  but doesn't change which references the mark phase has to follow.
- **VCall correctness**: `cross_thread_smoke` + the cross-zpkg
  vcall_base_fallback test cover the dispatch paths affected by the
  header migration.
- **Benchmark**: bench/scenarios/05_polymorphic_dispatch.z42 (the PIC
  4-way bench) should show a small interp speedup from removing the
  per-VCall lock; JIT speedup will land in the follow-up PIC-inline
  spec.

## Out of Scope (carried over from proposal)

See proposal.md "Out of Scope" — no changes.
