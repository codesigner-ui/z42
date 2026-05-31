# Spec: ScriptObject header / body split

## MODIFIED Requirements

### Requirement: ScriptObject layout split

`ScriptObject` is split into two storage regions on the heap:

1. **Header** (immutable, lockless): `type_desc: Arc<TypeDesc>` and
   `type_args: Box<[String]>`.
2. **Body** (mutable, Mutex-protected): `slots: Vec<Value>` and
   `native: NativeData`.

#### Scenario: lockless read of type_desc
- **WHEN** any caller invokes `GcRef::type_desc(&obj)` (or the
  equivalent accessor on `ScriptObject`)
- **THEN** the call returns a `&TypeDesc` reference without taking any
  `Mutex`, `parking_lot` lock, or other synchronization primitive on the
  per-entry body mutex. It does take whatever ownership-preserving
  load is needed to reach the Arc's payload (one pointer deref +
  one pointer deref).

#### Scenario: lockless read of type_args
- **WHEN** any caller invokes `GcRef::type_args(&obj)`
- **THEN** the call returns a `&[String]` slice without taking the body
  mutex.

#### Scenario: body still locked
- **WHEN** any caller invokes `obj.borrow()` or `obj.borrow_mut()`
- **THEN** the body Mutex is acquired exactly as before; the returned
  guard exposes `slots` and `native` for read or mutation.

#### Scenario: header write-once invariant
- **WHEN** `alloc_object` runs
- **THEN** it initializes the header with the supplied type_desc and
  type_args exactly once. The header has no mutation API exposed to
  callers — there is no `set_type_desc` or `set_type_args`.

### Requirement: GC mark traversal walks the header without locking

#### Scenario: collector marks type_desc Arc
- **WHEN** the mark phase visits a live `RegionEntry<ScriptObject>`
- **THEN** it reads the header's `type_desc` Arc without locking the
  body Mutex, queues it for traversal (the `TypeDesc` keeps any nested
  references alive), and proceeds to lock the body solely to walk the
  slots.

#### Scenario: collector marks slots inside body
- **WHEN** the mark phase walks slots
- **THEN** it locks the body Mutex, iterates `slots`, marks each
  `Value` if it is a heap reference, and releases the lock — unchanged
  from current behavior modulo the type_desc step happening before.

### Requirement: PIC IC scan can read type_id without locking

#### Scenario: jit_vcall IC fast path
- **WHEN** `jit_vcall` checks the VCallIC for a cached type_id match
- **THEN** it reads the receiver's `type_desc.id.0` via the new
  lockless accessor; the previous `rc.borrow().type_desc.id.0` lock
  acquisition is removed.

#### Scenario: interp vcall IC fast path
- **WHEN** `exec_vcall::vcall` checks the VCallIC in the interp
- **THEN** it reads `type_id` via the same lockless accessor; the
  existing PIC behavior (4-slot scan, round-robin install, UNRESOLVED
  early exit) is unchanged.

#### Scenario: FieldGet / FieldSet PIC fast path
- **WHEN** `exec_object::field_get` / `field_set` or the JIT helpers
  `jit_field_get` / `jit_field_set` consult the FieldIC
- **THEN** they read `type_id` via the lockless accessor. Slot read /
  write still uses `obj.borrow()` / `obj.borrow_mut()` because slots
  live in the body.

### Requirement: backward-compatible API surface

#### Scenario: existing `obj.borrow().type_desc` usages
- **WHEN** legacy call sites still write `obj.borrow().type_desc`
- **THEN** the code compiles unchanged for the duration of the
  migration (the borrow guard exposes the same `type_desc` field name
  via a delegating accessor). Once the migration is complete, the
  delegating accessor is removed and call sites are required to use
  `GcRef::type_desc` or the equivalent.

## Pipeline Steps

- [ ] Lexer — no change
- [ ] Parser / AST — no change
- [ ] TypeChecker — no change
- [ ] IR Codegen — no change (zbc / zpkg format unchanged)
- [x] VM interp — migrates 4 read sites + verifies cross-zpkg dispatch
- [x] GC — mark traversal reads header outside lock; sweep unchanged
- [x] JIT helpers — migrate FieldGet/Set, VCall, IsInstance, AsCast
  reads; PIC scan paths simplified
