# Design: Object Base Class and Type Descriptor

## Architecture

```
z42.core stdlib (.z42)            VM builtins (Rust)
─────────────────────────         ──────────────────────────────
Object
  GetType()  [Native]  ────────►  __obj_get_type(this)
                                    → Value::Object { class_name="z42.core.Type",
                                                      __name=<simple>, __fullName=<fqn> }
  ReferenceEquals() [Native] ──►  __obj_ref_eq(a, b)
                                    → Rc::ptr_eq  (both null ⇒ true)
  GetHashCode() [Native] ──────►  __obj_hash_code(this)
                                    → Rc::as_ptr() & 0x7fff_ffff
  Equals(Object?)                 (pure z42: delegates to ReferenceEquals)
  ToString()                      (pure z42: delegates to GetType().Name)

Type (sealed)
  __name / __fullName             VM writes these fields directly in __obj_get_type
  Name  => __name
  FullName => __fullName
```

## Decisions

### Decision 1: Type as a plain Object with hidden fields
**Problem:** How to return typed `Type` instances from `__obj_get_type`?
**Options:**
- A — Add a new `Value::TypeDescriptor(name)` variant to the Value enum.
- B — Create a `Value::Object` with `class_name = "z42.core.Type"` and VM-set private fields.
**Decision:** B — avoids touching the Value enum and bytecode format; VM creates the object
directly, bypassing the constructor. Works with existing `FieldGet` for property reads.

### Decision 2: GetHashCode as virtual extern
**Problem:** Base implementation needs a native identity hash, but subclasses must be able to override.
**Decision:** `public virtual extern int GetHashCode()` — IrGen emits a native stub named
`z42.core.Object.GetHashCode`; VCall walks the hierarchy and finds overrides first.
TypeChecker currently has no restriction on `virtual extern`, so this is valid.

### Decision 3: Struct separation
**Decision:** Struct types do NOT inherit Object. Compiler will synthesise value-semantic
`Equals`/`GetHashCode`/`ToString` for structs when struct TypeChecker is implemented (L1 later).

## Implementation Notes
- `builtin_obj_get_type`: extracts `ObjectData::class_name`, splits on `.` to derive simple name,
  creates a fresh `ObjectData` with `class_name = "z42.core.Type"` and two hidden fields.
- `builtin_obj_ref_eq`: uses `Rc::ptr_eq`; null+null ⇒ true; null+non-null ⇒ false.
- `builtin_obj_hash_code`: uses `Rc::as_ptr() as i64 & 0x7fff_ffff` cast to `Value::I32`.
- `NativeTable` param counts include `this` for instance methods.

## Testing Strategy
- Unit tests in `z42.Tests/IrGenTests.cs` (or new `StdlibTests.cs`): verify Object methods compile.
- z42 example file `examples/object_protocol.z42` exercising GetType, Equals, GetHashCode.
- `dotnet test + ./scripts/test-vm.sh` — full green before archive.
