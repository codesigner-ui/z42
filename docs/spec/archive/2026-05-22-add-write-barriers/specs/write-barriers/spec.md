# Spec: GC Write Barriers

## ADDED Requirements

### Requirement: Field write barrier fires from interp FieldSet

#### Scenario: Setting an object field to a heap reference triggers the barrier
- **WHEN** interp executes `FieldSet obj.field = ref_value` where `ref_value` is `Value::Object / Array / Closure / Ref / WeakRef`
- **THEN** `MagrGC::write_barrier_field(owner=Value::Object(obj), slot=<resolved slot>, new=&ref_value)` is called exactly once
- **AND** the underlying slot write completes (no behavior change vs. pre-barrier code)

#### Scenario: Setting an object field to a primitive does NOT trigger the barrier
- **WHEN** interp executes `FieldSet obj.field = primitive_value` (`I64 / F64 / Bool / Char / Str / Null` — anything `Value::is_heap_ref() == false`)
- **THEN** `MagrGC::write_barrier_field(...)` is **not** called — primitive writes neither change cross-region references (card marking 无关) nor overwrite a tracked snapshot edge in any way SATB-style impls would care about
- **AND** the underlying slot write completes normally
- **AND** trait override impls may `debug_assert!(new.is_heap_ref())` to detect callers bypassing the contract

#### Scenario: IC fast path also dispatches the barrier
- **WHEN** the FieldSet IC fast path hits (cached type match) and writes the slot directly
- **THEN** the barrier fires on the fast path too — both IC fast and slow paths must invoke `write_barrier_field` so observer counts are correct

### Requirement: Array element barrier fires from interp ArraySet

#### Scenario: Setting an array element triggers the barrier
- **WHEN** interp executes `ArraySet arr[i] = v`
- **THEN** `MagrGC::write_barrier_array_elem(arr=Value::Array(...), idx=i, new=&v)` is called exactly once
- **AND** the underlying element write completes

### Requirement: JIT field / array writes dispatch the barrier

#### Scenario: JIT-compiled FieldSet fires the field barrier
- **WHEN** Cranelift JIT executes a FieldSet through `jit_field_set`
- **THEN** the same `write_barrier_field` call fires (via the heap reference passed to the helper)
- **AND** the slot write completes

#### Scenario: JIT-compiled ArraySet fires the array barrier
- **WHEN** Cranelift JIT executes an ArraySet through `jit_array_set`
- **THEN** `write_barrier_array_elem` fires with the same arguments as the interp path

### Requirement: Barrier no-op default preserves behavior

#### Scenario: Default ArcMagrGC barrier impl does not change GC behavior
- **WHEN** the default `MagrGC::write_barrier_*` methods (no-op) are in effect (the production `ArcMagrGC` case)
- **THEN** every existing arc_heap_tests / cross-thread / stdlib / golden test stays GREEN with bit-identical output
- **AND** GC stats (`used_bytes`, `allocations`, `gc_cycles`) are unchanged from pre-barrier-wiring

### Requirement: Observer test fixture proves dispatch

#### Scenario: Test-only observer counts barrier calls
- **WHEN** a unit test installs a test-only `ArcMagrGC::barrier_observer` (closure) and runs a script-like sequence of field/array writes
- **THEN** the observer's recorded calls match a known-good sequence (count + ordering) for that fixture
- **AND** removing the observer restores production behavior (no leak across tests)

## MODIFIED Requirements

### Requirement: `MagrGC::write_barrier_*` callers contract

**Before** (current): Trait methods exist with no-op defaults. Spec
says "Phase 2+ uses for generational / SATB" but call sites are not
wired. Embedders mutating heap directly (interp / JIT) do not invoke.

**After**: Trait methods MUST be invoked **after** every heap-object
slot write and array element write — including IC fast path, JIT
helpers, and any future write code path — **iff** the new value is a
heap reference (`Value::is_heap_ref()` returns `true`). Primitive
writes are exempt: callers MUST skip the dispatch (Decision 1).

The trait method may dispatch the work (default no-op for STW
mark-sweep; future generational / concurrent backends override).
Callers pass the `&Value` reference at the time of the write so the
overriding impl sees the new value. The write happens before the
barrier call so SATB-style impls can also read the old slot if needed
via `owner.borrow().slots[slot]` — but the call signature does NOT
carry the old value; SATB backends will need to extend the trait, out
of scope for this spec.

## Pipeline Steps

受影响的 pipeline 阶段：

- [ ] Lexer — 不变
- [ ] Parser / AST — 不变
- [ ] TypeChecker — 不变
- [ ] IR Codegen — 不变（barrier 由 runtime 加，不影响 IR）
- [x] VM interp — exec_object.rs / exec_array.rs 三处插入 barrier 调用
- [x] JIT — jit_field_set / jit_array_set 两处 helper 插入 barrier 调用

## IR Mapping

无新 IR 指令。`FieldSet` / `ArraySet` / `StaticSet` 字节码保持不变；
barrier 调用纯运行时 wiring，编译器 emit 端零变化。
