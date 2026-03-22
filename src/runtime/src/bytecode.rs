use crate::types::ExecMode;
use serde::{Deserialize, Serialize};

/// Magic bytes for future binary .z42bc format: "Z42\0"
pub const MAGIC: [u8; 4] = [0x5A, 0x34, 0x32, 0x00];

/// Top-level bytecode module.
/// Phase 1: loaded from `.z42ir.json`
/// Phase 2: loaded from `.z42bc` binary
#[derive(Debug, Serialize, Deserialize)]
pub struct Module {
    pub name: String,
    pub string_pool: Vec<String>,
    #[serde(default)]
    pub classes: Vec<ClassDesc>,
    pub functions: Vec<Function>,
}

/// Class descriptor — field layout for object allocation.
#[derive(Debug, Serialize, Deserialize)]
pub struct ClassDesc {
    pub name: String,
    #[serde(default)]
    pub base_class: Option<String>,
    pub fields: Vec<FieldDesc>,
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
}

/// One row in a function's exception table.
#[derive(Debug, Serialize, Deserialize)]
pub struct ExceptionEntry {
    pub try_start:   String,
    pub try_end:     String,
    pub catch_label: String,
    pub catch_type:  Option<String>,
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
#[derive(Debug, Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "snake_case")]
pub enum Instruction {
    // Constants
    ConstStr  { dst: Reg, idx: u32 },
    ConstI32  { dst: Reg, val: i32 },
    ConstI64  { dst: Reg, val: i64 },
    ConstF64  { dst: Reg, val: f64 },
    ConstBool { dst: Reg, val: bool },
    ConstNull { dst: Reg },
    Copy      { dst: Reg, src: Reg },
    // Arithmetic
    Add { dst: Reg, a: Reg, b: Reg },
    Sub { dst: Reg, a: Reg, b: Reg },
    Mul { dst: Reg, a: Reg, b: Reg },
    Div { dst: Reg, a: Reg, b: Reg },
    Rem { dst: Reg, a: Reg, b: Reg },
    // Comparison
    Eq { dst: Reg, a: Reg, b: Reg },
    Ne { dst: Reg, a: Reg, b: Reg },
    Lt { dst: Reg, a: Reg, b: Reg },
    Le { dst: Reg, a: Reg, b: Reg },
    Gt { dst: Reg, a: Reg, b: Reg },
    Ge { dst: Reg, a: Reg, b: Reg },
    // Logical
    And { dst: Reg, a: Reg, b: Reg },
    Or  { dst: Reg, a: Reg, b: Reg },
    Not { dst: Reg, src: Reg },
    // Unary arithmetic
    Neg { dst: Reg, src: Reg },
    // Bitwise
    BitAnd { dst: Reg, a: Reg, b: Reg },
    BitOr  { dst: Reg, a: Reg, b: Reg },
    BitXor { dst: Reg, a: Reg, b: Reg },
    BitNot { dst: Reg, src: Reg },
    Shl    { dst: Reg, a: Reg, b: Reg },
    Shr    { dst: Reg, a: Reg, b: Reg },
    // Mutable variable slots (for locals that cross basic block boundaries)
    Store { var: String, src: Reg },
    Load  { dst: Reg, var: String },
    // String
    StrConcat { dst: Reg, a: Reg, b: Reg },
    ToStr     { dst: Reg, src: Reg },
    // Calls
    Call    { dst: Reg, func: String, args: Vec<Reg> },
    Builtin { dst: Reg, name: String, args: Vec<Reg> },
    // Arrays
    /// Allocate a zero-initialised array of `size` elements.
    ArrayNew    { dst: Reg, size: Reg },
    /// Allocate an array from a literal list of element registers.
    ArrayNewLit { dst: Reg, elems: Vec<Reg> },
    /// Load element at `idx` from array `arr` into `dst`. Panics on out-of-bounds.
    ArrayGet    { dst: Reg, arr: Reg, idx: Reg },
    /// Store `val` into array `arr` at `idx`. Panics on out-of-bounds.
    ArraySet    { arr: Reg, idx: Reg, val: Reg },
    /// Load the length of array `arr` as i32 into `dst`.
    ArrayLen    { dst: Reg, arr: Reg },
    // Objects
    /// Allocate a new object of `class_name`, calling its constructor with `args`.
    ObjNew   { dst: Reg, class_name: String, args: Vec<Reg> },
    /// Load field `field_name` of object `obj` into `dst`.
    FieldGet { dst: Reg, obj: Reg, field_name: String },
    /// Store `val` into field `field_name` of object `obj`.
    FieldSet { obj: Reg, field_name: String, val: Reg },
    /// Virtual dispatch: invoke `method` on runtime class of `obj`, walking base classes.
    VCall    { dst: Reg, obj: Reg, method: String, args: Vec<Reg> },
    /// `expr is ClassName` — dst = true if obj's runtime type is class_name or a subclass.
    IsInstance { dst: Reg, obj: Reg, class_name: String },
    /// `expr as ClassName` — dst = obj if it is an instance of class_name (or subclass), else null.
    AsCast     { dst: Reg, obj: Reg, class_name: String },
}

/// Block terminator.
#[derive(Debug, Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "snake_case")]
pub enum Terminator {
    Ret    { reg: Option<Reg> },
    Br     { label: String },
    BrCond { cond: Reg, true_label: String, false_label: String },
    /// Throw the value in `reg` as an exception.
    Throw  { reg: Reg },
}
