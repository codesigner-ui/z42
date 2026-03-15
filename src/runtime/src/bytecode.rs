use crate::types::ExecMode;
use serde::{Deserialize, Serialize};

/// Magic bytes at the start of every .z42bc file: "Z42\0"
pub const MAGIC: [u8; 4] = [0x5A, 0x34, 0x32, 0x00];

/// Top-level bytecode module loaded from a .z42bc file.
#[derive(Debug, Serialize, Deserialize)]
pub struct Module {
    pub name: String,
    pub version: (u16, u16),
    pub functions: Vec<Function>,
    pub string_pool: Vec<String>,
}

/// A single function in the module.
#[derive(Debug, Serialize, Deserialize)]
pub struct Function {
    pub name: String,
    pub params: Vec<TypeId>,
    pub ret: TypeId,
    pub exec_mode: ExecMode,
    pub blocks: Vec<BasicBlock>,
}

/// A basic block — a straight-line sequence of instructions with one terminator.
#[derive(Debug, Serialize, Deserialize)]
pub struct BasicBlock {
    pub label: String,
    pub instructions: Vec<Instruction>,
    pub terminator: Terminator,
}

/// Typed register reference.
pub type Reg = u32;

/// Type identifier (index into a type table, TBD).
pub type TypeId = u32;

/// SSA instructions (subset — to be expanded).
#[derive(Debug, Serialize, Deserialize)]
pub enum Instruction {
    // Constants
    ConstI32(Reg, i32),
    ConstI64(Reg, i64),
    ConstF64(Reg, f64),
    ConstBool(Reg, bool),
    ConstStr(Reg, u32), // index into string pool

    // Arithmetic
    Add(Reg, Reg, Reg),
    Sub(Reg, Reg, Reg),
    Mul(Reg, Reg, Reg),
    Div(Reg, Reg, Reg),

    // Comparison
    Eq(Reg, Reg, Reg),
    Lt(Reg, Reg, Reg),

    // Calls
    Call(Reg, String, Vec<Reg>),
}

/// Block terminator.
#[derive(Debug, Serialize, Deserialize)]
pub enum Terminator {
    Ret(Option<Reg>),
    Br(String),
    BrCond(Reg, String, String),
}
