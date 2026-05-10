# Proposal: Add Object Base Class and Type Descriptor

## Why
The current `Object` stub is incomplete: `ToString()` returns `""` instead of the type name,
`Equals` doesn't accept null safely, and there is no `GetType()` / `ReferenceEquals()`.
Without these, string interpolation falls back to empty strings, identity comparison requires
VM-only `is_instance`/`as_cast`, and introspection is impossible in z42 code.

## What Changes
- `Object.z42`: add `GetType()`, static `ReferenceEquals()`, fix `Equals(Object?)` nullable,
  fix `ToString()` to return the type name, add `[Native]` `GetHashCode()` with identity hash.
- `Type.z42` (new): lightweight runtime type descriptor with `Name` and `FullName` properties.
- `NativeTable.cs`: register `__obj_get_type`, `__obj_ref_eq`, `__obj_hash_code`.
- `builtins.rs`: implement the three new VM intrinsics.

## Scope
| File/Module | Change type | Notes |
|-------------|-------------|-------|
| `src/libraries/z42.core/src/Object.z42` | modify | add 3 native methods, fix 2 |
| `src/libraries/z42.core/src/Type.z42` | new | Name + FullName properties |
| `src/compiler/z42.IR/NativeTable.cs` | modify | +3 entries |
| `src/runtime/src/interp/builtins.rs` | modify | +3 builtin functions |

## Out of Scope
- `IDisposable` (L2), generic `IEquatable<T>` / `IComparable<T>` (L3)
- `Type.IsAssignableTo()` — needs module context in builtins; deferred to L2
- Struct value-semantic Equals/GetHashCode (struct TypeChecker not yet implemented)
- Operator `==` overloading (L2)
