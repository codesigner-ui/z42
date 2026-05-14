# Spec: cross-zpkg subclass field inheritance

## ADDED Requirements

### Requirement: Cross-zpkg subclass field layout includes inherited fields

When a class `Sub` in zpkg `B` declares `: Base` where `Base` lives in zpkg `A` (`A != B`), `Sub`'s runtime `TypeDesc.fields` and `TypeDesc.field_index` MUST include all of `Base`'s fields (recursively up the inheritance chain), arranged so base fields occupy lower slot indices than `Sub`'s own declared fields. Field-name collisions between base and subclass remain disallowed (subclass-declared field with the same name as an inherited field is a load-time error — same rule as same-zpkg subclasses).

#### Scenario: Subclass in dependent zpkg gets inherited fields

- **WHEN** zpkg `A` defines `class Base { string Name; int Age; }`
- **AND** zpkg `B` (with `[dependencies] "A" = "..."`) defines `class Sub : Base { bool Flag; }`
- **AND** both zpkgs are loaded into the same VmContext (either order)
- **THEN** `TypeDesc(Sub).field_index` contains entries `Name → 0`, `Age → 1`, `Flag → 2`
- **AND** `new Sub()` allocates an object with 3 field slots
- **AND** `field.set %0 @Name "x"` (compiled inside zpkg `A`'s Base ctor) on a `Sub` instance writes "x" into slot 0 of the instance and is readable as `instance.Name == "x"` from any caller

#### Scenario: Three-level cross-zpkg chain

- **WHEN** zpkg `A` defines `class Base { string A; }`
- **AND** zpkg `B` (depends on A) defines `class Mid : Base { string B; }`
- **AND** zpkg `C` (depends on B) defines `class Leaf : Mid { string C; }`
- **THEN** `TypeDesc(Leaf).fields` has 3 entries in order `[A, B, C]`
- **AND** all three field accesses (`.A`, `.B`, `.C`) succeed from any of A/B/C code

#### Scenario: Subclass ctor `: base(arg)` propagates to inherited field

- **WHEN** zpkg `A` defines `class Exception { string Message; Exception(string m) { this.Message = m; } }`
- **AND** zpkg `B` defines `class MyExc : Exception { MyExc(string m) : base(m) {} }`
- **THEN** `var e = new MyExc("hello"); e.Message == "hello"` evaluates true
- **AND** the same holds if `B`'s ctor body also has `this.Message = "override";` (the field write reaches the same physical slot)

### Requirement: Vtable inheritance across zpkgs

When a subclass in zpkg `B` extends a base in zpkg `A`, `Sub`'s runtime `TypeDesc.vtable` MUST include base methods at the same vtable slot indices as `Base`'s own vtable. Subclass overrides occupy the same slot as the base method they override (subclass adds to the end for non-overridden methods).

#### Scenario: Virtual dispatch through inherited slot

- **WHEN** zpkg `A` defines `class Base { virtual string Name() { return "base"; } }`
- **AND** zpkg `B` defines `class Sub : Base { override string Name() { return "sub"; } }`
- **AND** `Base x = new Sub();`
- **THEN** `x.Name()` invokes `Sub.Name` and returns `"sub"` (not `"base"`)

### Requirement: Fixup is idempotent and order-independent

The fixup pass that resolves cross-zpkg inheritance MUST be idempotent: running it twice on the same TypeDesc must produce the same result. The pass MUST be order-independent: loading zpkg `B` before `A` (where `B` depends on `A`) is already disallowed by the dep-load model, but if a subclass's base is not yet loaded when the subclass loads, the fixup MUST defer that subclass and retry whenever a new zpkg loads.

#### Scenario: Deferred fixup retries on later load

- **WHEN** zpkg `B` loads first containing `class Sub : Std.Base { ... }`, but `Std.Base` (in zpkg `A`) is not yet loaded
- **AND** then zpkg `A` loads
- **THEN** after `A` is loaded, `Sub.field_index` MUST contain `Base`'s fields
- **AND** there is no requirement to "reload" `B`'s zpkg file from disk; the fixup mutates the in-memory `TypeDesc` (or atomically replaces the Arc-wrapped descriptor)

### Requirement: Stable on-disk zbc format

This fix MUST NOT change the on-disk zbc format. The bytecode emitter continues to write only the class's own declared fields and methods into `module.classes[i]`. The merging of inherited slots happens entirely at runtime load.

## MODIFIED Requirements

**Before**: `build_type_registry` (in `src/runtime/src/metadata/loader.rs`) inherits base class fields and vtable from the local module's `registry` lookup, falling back to `unwrap_or_default()` (empty Vec) when the base is in another module. This produces silently-broken `TypeDesc.fields` / `field_index` / `vtable` / `vtable_index` for cross-zpkg subclasses.

**After**: `build_type_registry` populates **own declared fields only** into a new `own_fields` (and `own_vtable_entries`) field on `TypeDesc`. The publicly-facing `fields` / `field_index` / `vtable` / `vtable_index` are computed by a separate `resolve_inheritance` pass that runs:
1. **Eagerly** at module load (handles same-zpkg subclasses; was previously fused into build_type_registry)
2. **Re-runs** at lazy-load time after each new zpkg is registered, for any types whose base was previously unresolvable
3. **Idempotent** — repeated runs produce the same result

## VM Behaviour Notes

- `obj.new @ClassName` allocates `TypeDesc(ClassName).fields.len()` slots. After this fix, that count includes inherited slots for cross-zpkg subclasses.
- `field.get` / `field.set` use compile-time `FieldId` tokens. The compiler emits the FieldId based on its compile-time tsig view of the type's field layout. The tsig already reports inherited slots (cross-zpkg base classes have their tsig consulted during dependent zpkg compilation), so compiler-side FieldId allocation is already correct. The bug is purely runtime — the runtime `TypeDesc` failed to mirror the compile-time layout.

## Pipeline Steps

Receives no change to compile pipeline. Runtime-only fix.
- [ ] Lexer — no change
- [ ] Parser / AST — no change
- [ ] TypeChecker — no change
- [ ] IR Codegen — no change
- [x] VM interp — runtime field layout fix (loader + lazy_loader)
