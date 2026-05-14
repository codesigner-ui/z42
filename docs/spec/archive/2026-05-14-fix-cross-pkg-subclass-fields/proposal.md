# Proposal: Fix cross-zpkg subclass field inheritance

## Why

A subclass declared in one zpkg that inherits from a base class in another zpkg ends up with a broken field layout: inherited fields are not present in the subclass's `TypeDesc.fields` / `field_index`. At runtime, `field.set` against an inherited field name silently misses (writes to the wrong slot or out of bounds), so the field appears `null` to readers.

**Concrete reproduction** (verified 2026-05-14):
- `Std.Exception` (base) is in z42.core, with fields `Message`, `StackTrace`, `InnerException`.
- `Std.InvalidOperationException : Exception` in z42.core (same zpkg): `new InvalidOperationException("x").Message == "x"` ✅
- `Std.ProcessStartException : Exception` in z42.io (cross-zpkg): `new ProcessStartException("x").Message == null` ❌
- Even `this.Message = m;` explicitly in the z42.io subclass ctor does not propagate.

**Root cause** (from src/runtime/src/metadata/loader.rs:515–533):

```rust
let mut fields: Vec<FieldSlot> = desc.base_class
    .as_deref()
    .and_then(|b| registry.get(b))    // ← local module registry only
    .map(|td| td.fields.clone())
    .unwrap_or_default();              // ← cross-zpkg base ⇒ empty Vec
```

`build_type_registry` runs once per module at load time. When z42.io loads, `Std.Exception` is not yet in z42.io's local registry — `registry.get("Std.Exception")` returns `None`. The subclass's `fields` Vec is built without inherited entries, and `field_index` reflects only the subclass's own declared fields.

This is the first stdlib case where a subclass crosses package boundaries (add-std-process introduced four exception classes in z42.io extending z42.core's Exception). All prior stdlib subclasses lived in the same zpkg as their base.

Blast radius: any z42 user code that subclasses a stdlib type from a separate library/app zpkg is silently broken. We need this fixed before z42.toml / z42.json / user code patterns of "extend Std.Exception" multiply the bug.

## What Changes

1. Add a **fixup pass** at lazy-load time that walks newly-loaded `TypeDesc`s and rebuilds `fields` / `field_index` / `vtable` / `vtable_index` to include the inherited entries from cross-zpkg base classes (now resolvable via the global `type_registry`).
2. The fixup runs **after** the dep zpkg(s) have loaded — i.e. at the point z42.io is registered into the global `LazyLoader.type_registry`, walk all of z42.io's TypeDescs and re-resolve their base_class chains.
3. Field/vtable layout becomes deterministic: base fields/methods always occupy the same low slot indices across all subclasses regardless of which zpkg defines the subclass.
4. Existing same-zpkg subclasses are unaffected (their `build_type_registry` pass already populated correctly; the fixup is idempotent).
5. Add a regression test: a z42.io subclass of `Std.Exception` must see `Message` propagated through `: base(msg)`.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/metadata/loader.rs` | MODIFY | Add `fixup_cross_zpkg_inheritance` helper; optionally split base-class field/vtable inheritance out of `build_type_registry` so it can run twice (first locally, second from global registry) |
| `src/runtime/src/metadata/lazy_loader.rs` | MODIFY | After inserting a zpkg's TypeDescs into `type_registry` (line 249–256), invoke the fixup pass over the newly-inserted types |
| `src/runtime/src/metadata/types.rs` | MODIFY | If `TypeDesc.fields` / `field_index` / `vtable` / `vtable_index` need to become `RefCell` / `OnceCell` (because they're inside `Arc`), update accordingly. Prefer: rebuild a new `TypeDesc` and replace via `Arc` swap |
| `src/runtime/src/metadata/loader_tests.rs` | MODIFY | Add a Rust-level unit test reproducing the cross-zpkg field-inheritance bug: build two synthetic modules (base in module A, subclass in module B), run fixup, assert subclass's `field_index` contains base's fields |
| `src/runtime/src/metadata/lazy_loader_tests.rs` (if exists) | MODIFY | End-to-end: load two zpkgs, verify subclass TypeDesc has inherited slots |
| `src/libraries/z42.io/tests/process_failure.z42` | MODIFY | Remove the test-disable workaround (currently the test is in WIP state — verify it now passes) |
| `docs/design/runtime/vm-architecture.md` | MODIFY | Document the two-phase type loading: "skeleton at module load, fixup at lazy-load merge" |

**只读引用**：
- `src/runtime/src/metadata/types.rs` — current `TypeDesc` / `FieldSlot` definitions
- `src/runtime/src/corelib/process.rs` — context (downstream consumer)
- `src/libraries/z42.io/src/Exceptions/*.z42` — context (the 4 exception classes that surface the bug)

## Out of Scope

- **Same-zpkg subclass inheritance** — already works.
- **Cross-zpkg static field handling** — different code path (`static_fields` / `static_field_index`), already designed for cross-zpkg via `resolve_static_field_id`.
- **Generic type inheritance across zpkgs** — separate bug if it exists; tracked independently.
- **vtable dispatch correctness for cross-zpkg subclass overrides** — likely affected by the same bug; will be fixed in the same pass since we rebuild `vtable` / `vtable_index` together with `fields` (one would be incomplete without the other). But verifying override semantics is a follow-up if anything regresses.
- **Compile-time cross-zpkg layout checks** — out of scope; runtime fix is enough.

## Open Questions

- [ ] Should the fixup pass mutate `TypeDesc` in place (requires interior mutability on `Arc`-wrapped TypeDesc) or build a new `TypeDesc` and swap the `Arc`? Latter is cleaner but requires a brief atomic-swap window.
- [ ] If a subclass's base is **still unresolvable after fixup** (e.g., the base zpkg isn't loaded yet because it's transitively required but not eagerly loaded), what's the behavior — error at fixup, defer further, or fail at first field access? Recommend: deferred, retry fixup whenever a new zpkg loads, since lazy loading is the design.
- [ ] Does the fix need a zbc format version bump? Probably no — the fix is purely runtime; the on-disk format of `TypeDesc.fields` is unchanged (it's already the local declared fields only).
