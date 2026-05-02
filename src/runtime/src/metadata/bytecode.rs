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
}

impl ConstraintBundle {
    pub fn is_empty(&self) -> bool {
        !self.requires_class && !self.requires_struct
            && self.base_class.is_none() && self.interfaces.is_empty()
            && self.type_param_constraint.is_none()
            && !self.requires_constructor
            && !self.requires_enum
    }
}

/// A single field in a class descriptor.
#[derive(Debug, Serialize, Deserialize)]
pub struct FieldDesc {
    pub name: String,
    #[serde(rename = "type")]
    pub type_tag: String,
}

/// A single function.
#[derive(Debug, Serialize, Deserialize)]
pub struct Function {
    pub name: String,
    /// Number of parameters — they occupy registers 0..param_count-1 on entry.
    pub param_count: usize,
    /// Return type tag: "void", "str", "i32", "i64", "f64", "bool".
    pub ret_type: String,
    pub exec_mode: ExecMode,
    pub blocks: Vec<BasicBlock>,
    #[serde(default)]
    pub exception_table: Vec<ExceptionEntry>,
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
    pub line_table: Vec<LineEntry>,
    /// Debug info: maps register IDs to source-level variable names.
    #[serde(default)]
    pub local_vars: Vec<LocalVar>,
    /// Generic type parameter names: ["T"], ["K", "V"]. Empty for non-generic functions.
    #[serde(default)]
    pub type_params: Vec<String>,
    /// L3-G3a: constraint bundle per type parameter (aligned by index with `type_params`).
    #[serde(default)]
    pub type_param_constraints: Vec<ConstraintBundle>,
    /// Precomputed block label → index mapping. Not serialized; populated after module load.
    #[serde(skip)]
    pub block_index: std::collections::HashMap<String, usize>,
}

/// An entry in a function's local variable table: register `reg` holds variable `name`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LocalVar {
    pub name: String,
    pub reg:  u16,
}

/// An entry in a function's source-line mapping table.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LineEntry {
    pub block: u32,
    pub instr: u32,
    pub line:  u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub file:  Option<String>,
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
    // Calls
    Call {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        func: String,
        #[serde(with = "typed_reg_vec_serde")] args: Vec<Reg>,
    },
    Builtin {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        name: String,
        #[serde(with = "typed_reg_vec_serde")] args: Vec<Reg>,
    },
    /// Push a function-reference value onto a register. The runtime resolves
    /// `func` at call site (current usage: L2 no-capture lambda lifted as a
    /// module-level function). See docs/design/closure.md §6.
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
    /// docs/design/closure.md §6.
    CallIndirect {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] callee: Reg,
        #[serde(with = "typed_reg_vec_serde")] args: Vec<Reg>,
    },
    /// L3 closure tier-C: allocate an env from `captures`, build a closure
    /// value and write it to `dst`. See docs/design/closure.md §6.
    /// `stack_alloc=true` (impl-closure-l3-escape-stack): VM 走 frame-local
    /// arena → `Value::StackClosure`；否则 heap → `Value::Closure`。
    MkClos {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        fn_name: String,
        #[serde(with = "typed_reg_vec_serde")] captures: Vec<Reg>,
        #[serde(default)] stack_alloc: bool,
    },
    // Arrays
    /// Allocate a zero-initialised array of `size` elements.
    ArrayNew {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] size: Reg,
    },
    /// Allocate an array from a literal list of element registers.
    ArrayNewLit {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_vec_serde")] elems: Vec<Reg>,
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
        #[serde(with = "typed_reg_vec_serde")] args: Vec<Reg>,
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
        #[serde(with = "typed_reg_vec_serde")] args: Vec<Reg>,
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
        #[serde(with = "typed_reg_vec_serde")] args: Vec<Reg>,
    },
    /// Native-type vtable indirect call. `vtable_slot` is filled by the C5
    /// source generator at compile time so no name lookup happens at runtime.
    CallNativeVtable {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        #[serde(with = "typed_reg_serde")] recv: Reg,
        vtable_slot: u16,
        #[serde(with = "typed_reg_vec_serde")] args: Vec<Reg>,
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
