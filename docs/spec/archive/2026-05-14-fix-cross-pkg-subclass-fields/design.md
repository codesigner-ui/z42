# Design: Cross-zpkg subclass field inheritance fix

## Architecture

```
─────────────────── current (broken) ───────────────────
load_zpkg(A)         build_type_registry(A)    A.types in A.module.type_registry
load_zpkg(B depA)    build_type_registry(B)    B's subclass-of-A reads only B's local registry → empty fields
LazyLoader.register  copy A.types + B.types into global type_registry  (B's broken TypeDesc unchanged)

─────────────────── after fix ──────────────────────────
load_zpkg(A)         build_type_registry(A)
                       └─ populates own_fields + computes initial fields/field_index from local registry
                          (same-zpkg base classes resolve eagerly)
LazyLoader.register(A) copy A.types → global registry
                       └─ run fixup_inheritance(A.types, global registry)  [no-op for A — nothing to fix]
load_zpkg(B depA)    build_type_registry(B)
                       └─ B.Sub gets own_fields = [B's declared], fields = empty (cross-zpkg base unresolved)
LazyLoader.register(B) copy B.types → global registry
                       └─ run fixup_inheritance(B.types, global registry)
                          ├─ for B.Sub: base "Std.Base" now resolvable
                          ├─ fields = base.fields.clone() + own_fields
                          └─ field_index rebuilt; vtable rebuilt
```

Two-phase type loading (mirrors C# / Java's "skeleton then fixup" pattern for forward references / cross-assembly inheritance).

## Decisions

### Decision 1: Where to store "own declared fields" separately?

**Problem**: After fixup, `TypeDesc.fields` contains base + own. We need to re-run fixup if dep loads later, so we must remember which fields are "own" vs "inherited".

**Options**:
- **A**: Store `own_fields: Vec<FieldSlot>` as a new field on `TypeDesc`. Keep `fields` as the merged view. Cost: ~24 bytes per TypeDesc + clone overhead.
- **B**: Re-read from `module.classes[i].fields` every time. Cost: need to keep a back-pointer from `TypeDesc` to `ClassDecl`, or look up by class name.
- **C**: Encode "where base ends and own begins" as a single `own_start: usize` index into `fields`. Cost: minimal; rebuild requires knowing base's contribution by re-cloning from base.

**Decision**: **C**. Single `usize` per TypeDesc; cheap; rebuild is `fields = base.fields.clone(); fields.extend(self.fields[self.own_start..]); self.own_start = base.fields.len();`. Works for nested inheritance because each level's `own_start` is relative to its own merged `fields`.

> Wait — that doesn't work cleanly if base also changes (multi-level deferred fixup). Simpler is **A** (store own_fields explicitly). Worth the ~24 bytes for clarity. **Final: A.**

### Decision 2: Mutable TypeDesc inside `Arc`?

`type_registry` holds `Arc<TypeDesc>`. To mutate after the Arc is shared, we either:
- **A**: Make `TypeDesc.fields` / `field_index` etc. `RefCell` / `OnceLock`. Cost: thread-safety friction; we're single-threaded (`Rc` not `Arc` would suffice except `Arc` is already there for cross-thread sharing in tests).
- **B**: Build a new `TypeDesc` and swap the `Arc` in the global registry. Cost: any caller holding an `Arc<TypeDesc>` clone sees the stale view until they re-resolve.
- **C**: Hold the receiver's TypeDesc by name (HashMap key) at access sites, not by `Arc` clone — but most access sites already cache `Arc<TypeDesc>`.

**Decision**: **A** — wrap the mutable inheritance state in `OnceLock`. Specifically:
- `own_fields: Vec<FieldSlot>` — set once at build time, never mutates.
- `fields: OnceLock<Vec<FieldSlot>>` — set by fixup (idempotent: same value if rerun).
- `field_index: OnceLock<HashMap<String, usize>>` — same.
- Same `OnceLock` pattern for `vtable` / `vtable_index`.

Since fixup is idempotent and the merged value is deterministic given the loaded type chain, multiple writers race-free at the value level (each computes the same answer); `OnceLock::get_or_init` semantics work. Single-threaded VM means no actual race anyway.

Trade-off accepted: any code that reads `td.fields` before fixup runs sees an empty/None view. Need to ensure fixup runs **before** any cross-zpkg subclass's TypeDesc is consulted. Lazy-loader insertion order makes this natural: `register_zpkg` → `insert_types` → `run_fixup_for(new_types)` → only then return control.

### Decision 3: Fixup pass placement and granularity

Where to call:
- **A**: At the end of `LazyLoader::register_zpkg`, after `type_registry.insert(name, desc)` loop (line ~257).
- **B**: At first field access on a TypeDesc that hasn't been fixed up yet (lazy).

**Decision**: **A** — eager fixup at zpkg registration. Predictable, deterministic, avoids reentrancy hazards. The new-zpkg-load event also retries any previously-deferred subclasses (their base may have just landed).

Granularity: walk **all** loaded TypeDescs, not just the just-added batch. A new zpkg may unblock subclasses from an earlier batch. Cost: O(N total types × inheritance depth) per zpkg load. N is small (stdlib: ~100 classes; user code: hundreds at most). Acceptable.

### Decision 4: Behavior when base class is still unresolvable

If a subclass's base never loads (user error or transitive dep missing), what happens?

**Options**:
- **A**: Hard error at fixup ("base class `Std.Foo` not found").
- **B**: Leave `fields` / `field_index` empty (current broken behavior continues, but at least subsequent loads can fix it).
- **C**: Defer — register the unresolved subclass in a "pending" set; retry on every future zpkg load; warn (but don't error) at VM exit if any remain.

**Decision**: **C**. Lazy loading is the design intent — a base may load on first reference, not at startup. The pending-set approach plays naturally with that. At VM exit (or at first field access on an unresolved type, whichever comes first), emit a hard error: "type `B.Sub` references unresolvable base class `A.Foo`; ensure zpkg `A` is on the libs path".

### Decision 5: Vtable inheritance — same fix structure

The same bug afflicts `vtable` / `vtable_index` (also built in `build_type_registry`, lines 535-541). Apply the same own_X / OnceLock split to `vtable` / `vtable_index`. The override-slot logic (search `vtable_index` for the base method name) requires the inherited vtable to be present, so we must fixup vtable together with fields, not separately.

## Implementation Notes

### TypeDesc field changes (src/runtime/src/metadata/types.rs)

```rust
pub struct TypeDesc {
    pub name: String,
    pub base_class: Option<String>,
    // ── own declared (immutable post-build) ──
    pub own_fields: Vec<FieldSlot>,
    pub own_methods: Vec<MethodEntry>,   // module-local function names for this class's methods
    // ── merged (resolved by fixup) ──
    pub fields: OnceLock<Vec<FieldSlot>>,
    pub field_index: OnceLock<HashMap<String, usize>>,
    pub vtable: OnceLock<Vec<(String, String)>>,
    pub vtable_index: OnceLock<HashMap<String, usize>>,
    // ── other unchanged fields (interfaces, constraints, type_id, …) ──
    // ...
}

impl TypeDesc {
    /// Public accessor — panics if fixup hasn't run yet. Should only happen
    /// after `LazyLoader::register_zpkg` completes for this type's zpkg.
    pub fn fields(&self) -> &[FieldSlot] {
        self.fields.get().expect("TypeDesc.fields read before inheritance fixup")
    }
    pub fn field_index(&self) -> &HashMap<String, usize> {
        self.field_index.get().expect("TypeDesc.field_index read before fixup")
    }
    // similar for vtable / vtable_index
}
```

### Loader changes (src/runtime/src/metadata/loader.rs)

Split `build_type_registry` into:

```rust
pub fn build_type_registry(module: &mut Module) {
    // 1. Build own_fields / own_methods (purely from module.classes[i].fields / methods)
    // 2. Insert each TypeDesc into module.type_registry with fields/field_index/vtable/vtable_index = OnceLock::new() (empty)
    // 3. Call try_fixup_inheritance(&module.type_registry, &mut module.type_registry) — same-module fixup
    //    (recursive resolution within this module's own TypeDescs; cross-zpkg ones stay empty for now)
}

/// Try to fill in fields/field_index/vtable/vtable_index for any TypeDesc whose
/// base chain is fully resolvable in `global_registry`. Skips already-fixed types.
/// Returns the number of types newly fixed up.
pub fn try_fixup_inheritance(
    targets: impl Iterator<Item = &Arc<TypeDesc>>,
    global_registry: &HashMap<String, Arc<TypeDesc>>,
) -> usize {
    let mut newly_fixed = 0;
    for td in targets {
        if td.fields.get().is_some() { continue; }
        if let Some(merged) = compute_merged_layout(td, global_registry) {
            // OnceLock::set returns Err if already set — race-free idempotent.
            let _ = td.fields.set(merged.fields);
            let _ = td.field_index.set(merged.field_index);
            let _ = td.vtable.set(merged.vtable);
            let _ = td.vtable_index.set(merged.vtable_index);
            newly_fixed += 1;
        }
    }
    newly_fixed
}

fn compute_merged_layout(
    td: &TypeDesc, registry: &HashMap<String, Arc<TypeDesc>>,
) -> Option<MergedLayout> {
    // Recurse up base chain: each ancestor must already be fixed up.
    // Returns None if any ancestor isn't in registry or isn't fixed up yet.
}
```

### LazyLoader changes (src/runtime/src/metadata/lazy_loader.rs)

After line 257 (insert types into `self.type_registry`):

```rust
// Fixup pass: cross-zpkg subclasses may now be resolvable.
loop {
    let n = try_fixup_inheritance(self.type_registry.values(), &self.type_registry);
    if n == 0 { break; }   // fixed-point reached
}
```

The `loop` handles multi-level deferred chains (loading C might unblock B's fixup which unblocks A's).

### Lookup-site changes (everywhere `.fields` / `.field_index` is read)

`OnceLock` accessor swap. Use `td.fields()` / `td.field_index()` instead of `td.fields` / `td.field_index`. Compile-error driven — every site updates trivially. Expect ~10-15 sites across `interp/`, `corelib/`, `tests`.

## Testing Strategy

- **Rust unit test** (`loader_tests.rs`): Build two synthetic Modules (A: `class Base { x: i64 }`, B: `class Sub : Base { y: bool }`). Run `build_type_registry` on each, then simulate lazy-loader merge: insert all types into a shared HashMap, call `try_fixup_inheritance`. Assert `Sub.field_index` contains `{"x": 0, "y": 1}`.
- **Rust unit test (vtable)**: Same but with virtual methods. Assert vtable order is correct and override replaces base slot.
- **Rust unit test (3-level chain)**: A → B → C, all in separate modules. Assert `Leaf.field_index` has all 3 fields in correct order.
- **Rust unit test (deferred fixup)**: Load B first (no A), assert `Sub.field_index` is `None`. Load A. Assert fixup pass fills `Sub` correctly.
- **End-to-end (z42)**: Add a z42.io test that constructs `ProcessStartException("msg")` and asserts `Message` is "msg". Today this fails; after fix, must pass.
- **Stdlib regression**: All existing z42 tests must continue passing (no same-zpkg subclass should regress).
- **VM golden**: No new goldens needed (this is internal layout, not user-visible IR).

## Risks

- **`OnceLock` accessor migration is wide**: every site that reads `td.fields` / `td.field_index` must change. Compiler-driven so safe, but expect 2-3 rounds of "fix call site" iteration. Mitigation: split into a separate prep refactor commit if it becomes unwieldy.
- **vtable resolution interplay**: Subclass overrides depend on base's vtable_index being already populated. The `compute_merged_layout` recursion must process base before subclass — that's why it returns `None` if base isn't fixed up yet, deferring the subclass to the next pass.
- **JIT cache invalidation**: If JIT compiles a method before fixup completes and caches a field-slot index, the index may be wrong post-fixup. **Check**: does JIT cache slot indices? If yes, must invalidate JIT cache on fixup. (Likely safe: JIT is not currently used for stdlib paths; this is interp-only territory.)
- **Performance**: O(N × depth) fixup per zpkg load. For stdlib (8 zpkgs × ~100 types), ~10ms one-time cost. Negligible.
