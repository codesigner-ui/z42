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
    pub functions: Vec<Function>,
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
    // Mutable variable slots (for locals that cross basic block boundaries)
    Store { var: String, src: Reg },
    Load  { dst: Reg, var: String },
    // String
    StrConcat { dst: Reg, a: Reg, b: Reg },
    ToStr     { dst: Reg, src: Reg },
    // Calls
    Call    { dst: Reg, func: String, args: Vec<Reg> },
    Builtin { dst: Reg, name: String, args: Vec<Reg> },
}

/// Block terminator.
#[derive(Debug, Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "snake_case")]
pub enum Terminator {
    Ret    { reg: Option<Reg> },
    Br     { label: String },
    BrCond { cond: Reg, true_label: String, false_label: String },
}
