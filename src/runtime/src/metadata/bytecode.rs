use super::tokens::TypeId;
use super::types::{ExecMode, TypeDesc};
use super::bytecode_serde::{typed_reg_serde, typed_reg_vec_serde, typed_reg_opt_serde};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::Arc;

/// Top-level bytecode module.
/// Loaded from `.zbc` binary (or legacy `.z42ir.json`).
#[derive(Debug, Serialize, Deserialize)]
pub struct Module {
    pub name: String,
    pub string_pool: Vec<String>,
    #[serde(default)]
    pub classes: Vec<ClassDesc>,
    pub functions: Vec<Function>,
    /// Pre-built type descriptor registry — populated by the loader after
    /// deserialisation, not stored on disk.  Maps fully-qualified class name
    /// to the corresponding `TypeDesc` (field layout + vtable).
    #[serde(skip)]
    pub type_registry: HashMap<String, Arc<TypeDesc>>,
    /// Phase 3 S1 (`tokenize-ir-and-zbc-bump`, 2026-05-09): parallel
    /// by-`TypeId` view of the type registry. Index `i` holds the `Arc<TypeDesc>`
    /// whose `id == TypeId(i as u32)`. Built alongside `type_registry` by
    /// `build_type_registry` (intra-module classes) and extended by
    /// `register_lazy_type` (cross-zpkg lazy load).
    ///
    /// In S1 this is observability infrastructure — consumers still go
    /// through `type_registry` (HashMap by-name). S4 will switch hot paths
    /// to `type_by_id()` once IR fields are tokenised.
    #[serde(skip)]
    pub type_registry_vec: Vec<Arc<TypeDesc>>,
    /// Pre-built function name → index mapping for O(1) call dispatch.
    /// Populated by the loader after deserialisation.
    #[serde(skip)]
    pub func_index: HashMap<String, usize>,
    /// 2026-05-02 add-method-group-conversion (D1b): number of FuncRef cache
    /// slots required by `LoadFnCached` instructions. VM allocates a parallel
    /// `Vec<Value>` of this size on `VmContext` at module load.
    #[serde(default)]
    pub func_ref_cache_slots: u32,
}

impl Module {
    /// Phase 3 S1 (`tokenize-ir-and-zbc-bump`, 2026-05-09): O(1) by-`TypeId`
    /// type lookup. Invariant: `type_registry_vec[id.0] == registry[name]`
    /// where `name` is the FQ class name of that TypeDesc, maintained by
    /// `loader::build_type_registry` and `Module::register_lazy_type`.
    #[inline]
    pub fn type_by_id(&self, id: TypeId) -> Option<&Arc<TypeDesc>> {
        if !id.is_resolved() { return None; }
        self.type_registry_vec.get(id.0 as usize)
    }

    /// Append a lazily-loaded TypeDesc to both views (Vec and HashMap),
    /// assigning the next available `TypeId.0`. Returns the assigned id.
    /// Used by `lazy_loader` for cross-zpkg type resolution.
    ///
    /// Caller responsibility: the input `Arc<TypeDesc>` may carry its own
    /// `id` (from another module's build_type_registry); this method
    /// **rebuilds** the Arc with the freshly-allocated module-local id so
    /// downstream `td.id` checks remain consistent. If the type is already
    /// present (by-name match), returns the existing id without modification.
    pub fn register_lazy_type(&mut self, td: Arc<TypeDesc>) -> TypeId {
        if let Some(existing) = self.type_registry.get(&td.name) {
            return existing.id;
        }
        let new_id = TypeId(self.type_registry_vec.len() as u32);
        // Rebuild with the new module-local id (TypeDesc.id is a single u32 —
        // cheap to clone the rest by Arc-internals walking).
        let rebuilt = Arc::new(TypeDesc {
            name: td.name.clone(),
            id: new_id,
            base_name: td.base_name.clone(),
            fields: td.fields.clone(),
            field_index: td.field_index.clone(),
            vtable: td.vtable.clone(),
            vtable_index: td.vtable_index.clone(),
            own_fields: td.own_fields.clone(),
            own_methods: td.own_methods.clone(),
            type_params: td.type_params.clone(),
            type_args: td.type_args.clone(),
            type_param_constraints: td.type_param_constraints.clone(),
        });
        self.type_registry.insert(rebuilt.name.clone(), rebuilt.clone());
        self.type_registry_vec.push(rebuilt);
        new_id
    }
}

/// Class descriptor — field layout for object allocation.
#[derive(Debug, Serialize, Deserialize)]
pub struct ClassDesc {
    pub name: String,
    #[serde(default)]
    pub base_class: Option<String>,
    pub fields: Vec<FieldDesc>,
    /// Generic type parameter names: ["T"], ["K", "V"]. Empty for non-generic classes.
    #[serde(default)]
    pub type_params: Vec<String>,
    /// L3-G3a: constraint bundle per type parameter. When non-empty must align with
    /// `type_params` by index. Absent entries in old zbc deserialise as empty Vec.
    #[serde(default)]
    pub type_param_constraints: Vec<ConstraintBundle>,
}

/// Resolved constraint bundle for one generic type parameter. (L3-G3a, L3-G2.5 bare-tp)
/// Mirrors the C# `GenericConstraintBundle` on the semantic layer.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ConstraintBundle {
    #[serde(default)]
    pub requires_class: bool,
    #[serde(default)]
    pub requires_struct: bool,
    #[serde(default)]
    pub base_class: Option<String>,
    #[serde(default)]
    pub interfaces: Vec<String>,
    /// L3-G2.5 bare-typeparam: name of another type parameter in the same decl
    /// that this parameter must be a subtype of. None when no such constraint.
    #[serde(default)]
    pub type_param_constraint: Option<String>,
    /// L3-G2.5 ctor: `where T: new()` — type arg must have a no-arg constructor.
    #[serde(default)]
    pub requires_constructor: bool,
    /// L3-G2.5 enum: `where T: enum` — type arg must be an enum type.
    #[serde(default)]
    pub requires_enum: bool,
    /// add-generic-func-constraint (2026-05-11): function-type signature.
    /// `params` are IR type-name strings (e.g. "int", "string", "Cat"); `ret` is
    /// likewise a type name ("void" / "int" / etc.). None when no func constraint.
    #[serde(default)]
    pub func_signature: Option<FuncSigDescriptor>,
}

/// add-generic-func-constraint (2026-05-11): per-tp function signature spelled
/// as type-name strings (so zbc serialization is uniform with other constraint
/// fields that hold class/interface names).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct FuncSigDescriptor {
    pub params: Vec<String>,
    pub ret: String,
}

impl ConstraintBundle {
    pub fn is_empty(&self) -> bool {
        !self.requires_class && !self.requires_struct
            && self.base_class.is_none() && self.interfaces.is_empty()
            && self.type_param_constraint.is_none()
            && !self.requires_constructor
            && !self.requires_enum
            && self.func_signature.is_none()
    }
}

/// A single field in a class descriptor.
#[derive(Debug, Serialize, Deserialize)]
pub struct FieldDesc {
    pub name: String,
    #[serde(rename = "type")]
    pub type_tag: String,
}

/// Format a function's stack-trace display name with parameter signature.
/// Returns `<name>(<t1>,<t2>,...)` (e.g. `Demo.Greeter.greet(str)`).
/// Empty signature is `<name>()`. Used by VM frame push sites so traces
/// disambiguate overloads (1.3 split-debug-symbols Phase 4).
pub fn format_frame_name(func: &Function) -> String {
    let mut out = String::with_capacity(func.name.len() + 2 + func.param_count * 4);
    out.push_str(&func.name);
    out.push('(');
    for (i, t) in func.param_types.iter().enumerate() {
        if i > 0 { out.push(','); }
        out.push_str(t);
    }
    // When SIGS lacks per-param types (older artifacts or null source), fall
    // back to "?" placeholders matching `param_count` so the shape is
    // recognizable.
    if func.param_types.is_empty() && func.param_count > 0 {
        for i in 0..func.param_count {
            if i > 0 { out.push(','); }
            out.push('?');
        }
    }
    out.push(')');
    out
}

/// A single function.
#[derive(Debug, Serialize, Deserialize)]
pub struct Function {
    pub name: String,
    /// Number of parameters — they occupy registers 0..param_count-1 on entry.
    pub param_count: usize,
    /// Return type tag: "void", "str", "i32", "i64", "f64", "bool".
    pub ret_type: String,
    /// 1.3 split-debug-symbols: per-parameter type names for stack-trace
    /// signature decoration. Length always equals `param_count` (zbc writer
    /// pads unknowns with "?"). Empty when param_count == 0.
    ///
    /// review.md E5.1 (2026-05-26): immutable-after-construction → `Box<[T]>`
    /// saves 8 B/field/Function vs `Vec<T>` (no `cap` word). Six fields × 8 B
    /// ≈ 48 B per Function. Read-only consumers use `&[T]` via auto-deref.
    #[serde(default)]
    pub param_types: Box<[String]>,
    pub exec_mode: ExecMode,
    pub blocks: Vec<BasicBlock>,
    #[serde(default)]
    pub exception_table: Box<[ExceptionEntry]>,
    /// True for static class methods (no implicit `this` receiver).
    /// Instance methods have `this` as reg 0 and should not be treated as
    /// static-only entries in the StdlibCallIndex.
    #[serde(default)]
    pub is_static: bool,
    /// Total number of registers used (0 = unknown; VM falls back to dynamic sizing).
    #[serde(default)]
    pub max_reg: u32,
    /// Source-line mapping table (run-length encoded).
    /// Each entry: from (block_idx, instr_idx) onward, the source line is `line`.
    #[serde(default)]
    pub line_table: Box<[LineEntry]>,
    /// Debug info: maps register IDs to source-level variable names.
    #[serde(default)]
    pub local_vars: Box<[LocalVar]>,
    /// Generic type parameter names: ["T"], ["K", "V"]. Empty for non-generic functions.
    #[serde(default)]
    pub type_params: Box<[String]>,
    /// L3-G3a: constraint bundle per type parameter (aligned by index with `type_params`).
    #[serde(default)]
    pub type_param_constraints: Box<[ConstraintBundle]>,
    /// Precomputed block label → index mapping. Not serialized; populated after module load.
    #[serde(skip)]
    pub block_index: std::collections::HashMap<String, usize>,
    /// Per-function token cache (introduce-method-token, 2026-05-08).
    /// Lazy-init by `metadata::resolver::resolve_module` after module load.
    /// `OnceLock` so `Function: Sync` is preserved (single-thread today,
    /// future multi-thread ready). Not serialized — purely runtime metadata.
    #[serde(skip)]
    pub resolved: std::sync::OnceLock<super::resolver::ResolvedTokens>,
}

/// An entry in a function's local variable table: register `reg` holds variable `name`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LocalVar {
    pub name: String,
    pub reg:  u16,
}

/// An entry in a function's source-line mapping table.
///
/// 2026-05-10 span-column-propagate (zbc 1.1): `column` carries 1-based
/// source column from `Span.Column`. Value `0` means unknown (legacy
/// hand-rolled IR or pre-1.1 zbc never reach here — reader rejects).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LineEntry {
    pub block:  u32,
    pub instr:  u32,
    pub line:   u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub file:   Option<String>,
    #[serde(default)]
    pub column: u32,
}

/// One row in a function's exception table.
#[derive(Debug, Serialize, Deserialize)]
pub struct ExceptionEntry {
    pub try_start:   String,
    pub try_end:     String,
    pub catch_label: String,
    pub catch_type:  Option<String>,
    #[serde(with = "typed_reg_serde")]
    pub catch_reg:   u32,
}

/// A basic block — straight-line instructions ending in exactly one terminator.
#[derive(Debug, Serialize, Deserialize)]
pub struct BasicBlock {
    pub label: String,
    pub instructions: Vec<Instruction>,
    pub terminator: Terminator,
}

/// Register index.
pub type Reg = u32;

/// SSA instructions.
/// JSON wire format: {"op": "<snake_case_name>", <named fields...>}
///
/// Register fields accept both plain integers (`42`) and TypedReg objects
/// (`{"id": 42, "type": "i32"}`) during JSON deserialization for backward
/// compatibility with both old and new compiler output.
#[derive(Debug, Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "snake_case")]
pub enum Instruction {
    // Constants
    ConstStr  { #[serde(with = "typed_reg_serde")] dst: Reg, idx: u32 },
    ConstI32  { #[serde(with = "typed_reg_serde")] dst: Reg, val: i32 },
    ConstI64  { #[serde(with = "typed_reg_serde")] dst: Reg, val: i64 },
    ConstF64  { #[serde(with = "typed_reg_serde")] dst: Reg, val: f64 },
    ConstBool { #[serde(with = "typed_reg_serde")] dst: Reg, val: bool },
    ConstChar { #[serde(with = "typed_reg_serde")] dst: Reg, val: char },
    ConstNull { #[serde(with = "typed_reg_serde")] dst: Reg },
    Copy {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] src: Reg,
    },
    // Arithmetic
    Add {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Sub {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Mul {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Div {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Rem {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    // Comparison
    Eq {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Ne {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Lt {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Le {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Gt {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Ge {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    // Logical
    And {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Or {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Not {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] src: Reg,
    },
    // Unary arithmetic
    Neg {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] src: Reg,
    },
    // Bitwise
    BitAnd {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    BitOr {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    BitXor {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    BitNot {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] src: Reg,
    },
    Shl {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    Shr {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    // String
    StrConcat {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] a: Reg,
        #[serde(with = "typed_reg_serde")] b: Reg,
    },
    ToStr {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] src: Reg,
    },
    // Spec impl-ref-out-in-runtime: Address-load instructions producing
    // Value::Ref values. Caller emits these for `ref`/`out`/`in` arguments
    // before the Call; the Ref is passed through Call's args; callee's
    // frame.get/set transparently derefs (single dispatch point).
    LoadLocalAddr {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        /// Slot in the *current* frame to point at. Codegen guarantees this
        /// is a real local register (not virtual). At runtime produces
        /// `Value::Ref { kind: RefKind::Stack { frame_idx: depth-1, slot } }`.
        #[serde(with = "typed_reg_serde")] slot: Reg,
    },
    LoadElemAddr {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        /// Reg holding the array (must be `Value::Array(GcRef<...>)`).
        #[serde(with = "typed_reg_serde")] arr: Reg,
        /// Reg holding the index (must be `Value::I64`).
        #[serde(with = "typed_reg_serde")] idx: Reg,
    },
    LoadFieldAddr {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        /// Reg holding the object (must be `Value::Object(GcRef<...>)`).
        #[serde(with = "typed_reg_serde")] obj: Reg,
        field_name: String,
    },
    /// 2026-05-07 add-default-generic-typeparam (D-8b-3 Phase 2): runtime
    /// resolution of `default(T)` where T is a generic type-parameter of the
    /// receiver class. Reads `frame.regs[0]` (this) → `Object → type_desc.type_args[param_index]`,
    /// looks up the resolved type via `default_value_for(tag)`, writes Value to dst.
    /// Non-Object reg 0 / OOB index → graceful-degrade to `Value::Null`.
    DefaultOf {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        param_index: u8,
    },
    /// spec fix-numeric-cast-lowering (2026-05-13): explicit numeric type
    /// conversion. Target type comes from `dst`'s static type tag; source
    /// type is resolved at runtime from `src`'s `Value` variant.
    ///
    /// Covered:
    ///   - f64 → i*/u* (saturating, Rust `as` semantics; NaN → 0)
    ///   - i64 → f32/f64 (widening)
    ///   - i64 → i8/i16/i32 (low-bits + sign extend)
    ///   - i64 → u8/u16/u32 (low-bits + zero extend)
    ///   - char ↔ i32/i64 (Unicode scalar; invalid → error)
    /// Identity casts (fromIr == toIr) are not emitted — codegen returns the
    /// source register directly.
    Convert {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] src: Reg,
        /// Target type tag (TypeTags constants — I8/I16/.../F64/Char etc.).
        /// Source type is determined at runtime from `src`'s Value variant.
        to_tag: u8,
    },
    // Calls
    Call {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        func: String,
        #[serde(with = "typed_reg_vec_serde")] args: Box<[Reg]>,
    },
    Builtin {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        name: String,
        #[serde(with = "typed_reg_vec_serde")] args: Box<[Reg]>,
    },
    /// Push a function-reference value onto a register. The runtime resolves
    /// `func` at call site (current usage: L2 no-capture lambda lifted as a
    /// module-level function). See docs/design/language/closure.md §6.
    LoadFn {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        func: String,
    },
    /// 2026-05-02 add-method-group-conversion (D1b): cached method group
    /// conversion. First execution stores `Value::FuncRef(func)` into VmContext
    /// `func_ref_slots[slot_id]`; subsequent hits read from slot. Same fully-
    /// qualified `func` shares a `slot_id` across all call sites in a module.
    LoadFnCached {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        func: String,
        slot_id: u32,
    },
    /// Indirect call via a register holding a `FuncRef` value. See
    /// docs/design/language/closure.md §6.
    CallIndirect {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] callee: Reg,
        #[serde(with = "typed_reg_vec_serde")] args: Box<[Reg]>,
    },
    /// L3 closure tier-C: allocate an env from `captures`, build a closure
    /// value and write it to `dst`. See docs/design/language/closure.md §6.
    /// `stack_alloc=true` (impl-closure-l3-escape-stack): VM 走 frame-local
    /// arena → `Value::StackClosure`；否则 heap → `Value::Closure`。
    MkClos {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        fn_name: String,
        #[serde(with = "typed_reg_vec_serde")] captures: Box<[Reg]>,
        #[serde(default)] stack_alloc: bool,
    },
    // Arrays
    /// Allocate a zero-initialised array of `size` elements. Each slot is
    /// filled with the per-type default value derived from `elem_tag`
    /// (zbc `TypeTags::*` byte): `Value::I64(0)` for numeric tags,
    /// `Value::Bool(false)` for bool, `Value::Char('\0')` for char,
    /// `Value::F64(0.0)` for float/double, `Value::Null` for ref/string/unknown.
    /// fix-array-default-init, 2026-05-18.
    ArrayNew {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] size: Reg,
        #[serde(default)]
        elem_tag: u8,
    },
    /// Allocate an array from a literal list of element registers.
    ArrayNewLit {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_vec_serde")] elems: Box<[Reg]>,
    },
    /// Load element at `idx` from array `arr` into `dst`. Panics on out-of-bounds.
    ArrayGet {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] arr: Reg,
        #[serde(with = "typed_reg_serde")] idx: Reg,
    },
    /// Store `val` into array `arr` at `idx`. Panics on out-of-bounds.
    ArraySet {
        #[serde(with = "typed_reg_serde")] arr: Reg,
        #[serde(with = "typed_reg_serde")] idx: Reg,
        #[serde(with = "typed_reg_serde")] val: Reg,
    },
    /// Load the length of array `arr` as i32 into `dst`.
    ArrayLen {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] arr: Reg,
    },
    // Objects
    /// Allocate a new object of `class_name`, calling overload-resolved
    /// ctor `ctor_name` (FQ, 含 `$N` suffix 如有) with `args`. VM 不再做
    /// `${class}.${simple}` 名字推断 — 直查 `func_index[ctor_name]`.
    ObjNew {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        class_name: String,
        ctor_name: String,
        #[serde(with = "typed_reg_vec_serde")] args: Box<[Reg]>,
        /// 2026-05-07 add-default-generic-typeparam (D-8b-3 Phase 2): resolved
        /// generic type-arguments for this allocation, e.g. `["int"]` for
        /// `new Foo<int>()`. VM populates the new instance's
        /// `ScriptObject.type_args` from this list. Empty for non-generic.
        ///
        /// review.md E5.2 (2026-05-26): `Box<[String]>` instead of `Vec<String>`
        /// — immutable IR; saves 8 B/ObjNew. JIT helper takes `*const String`
        /// + `usize` from this storage (auto-deref ok since `Box<[T]>` ptr is
        /// the underlying T-array head).
        #[serde(default)]
        type_args: Box<[String]>,
    },
    /// Load field `field_name` of object `obj` into `dst`.
    FieldGet {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] obj: Reg,
        field_name: String,
    },
    /// Store `val` into field `field_name` of object `obj`.
    FieldSet {
        #[serde(with = "typed_reg_serde")] obj: Reg,
        field_name: String,
        #[serde(with = "typed_reg_serde")] val: Reg,
    },
    /// Virtual dispatch: invoke `method` on runtime class of `obj`, walking base classes.
    VCall {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] obj: Reg,
        method: String,
        #[serde(with = "typed_reg_vec_serde")] args: Box<[Reg]>,
    },
    /// `expr is ClassName` — dst = true if obj's runtime type is class_name or a subclass.
    IsInstance {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] obj: Reg,
        class_name: String,
    },
    /// `expr as ClassName` — dst = obj if it is an instance of class_name (or subclass), else null.
    AsCast {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] obj: Reg,
        class_name: String,
    },
    /// Load the module-level static field `field` into `dst`.
    StaticGet {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        field: String,
    },
    /// Store `val` into the module-level static field `field`.
    StaticSet {
        field: String,
        #[serde(with = "typed_reg_serde")] val: Reg,
    },

    // Native interop (C1 scaffold; semantics by C2/C4/C5)
    /// Direct native symbol call. Resolved at load time; runtime behaviour
    /// arrives in spec C2.
    CallNative {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        module: String,
        type_name: String,
        symbol: String,
        #[serde(with = "typed_reg_vec_serde")] args: Box<[Reg]>,
    },
    /// Native-type vtable indirect call. `vtable_slot` is filled by the C5
    /// source generator at compile time so no name lookup happens at runtime.
    CallNativeVtable {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] recv: Reg,
        vtable_slot: u16,
        #[serde(with = "typed_reg_vec_serde")] args: Box<[Reg]>,
    },
    /// Pin a String/Array buffer for FFI borrow. Pinned-view layout and
    /// lifetime semantics land in spec C4.
    PinPtr {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] src: Reg,
    },
    /// Release a pinned view created by `PinPtr`.
    UnpinPtr {
        #[serde(with = "typed_reg_serde")] pinned: Reg,
    },
}

/// Block terminator.
#[derive(Debug, Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "snake_case")]
pub enum Terminator {
    Ret {
        #[serde(with = "typed_reg_opt_serde")]
        reg: Option<Reg>,
    },
    Br { label: String },
    BrCond {
        #[serde(with = "typed_reg_serde")] cond: Reg,
        true_label: String,
        false_label: String,
    },
    /// Throw the value in `reg` as an exception.
    Throw {
        #[serde(with = "typed_reg_serde")] reg: Reg,
    },
}
