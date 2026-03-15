use crate::bytecode::{Function, Module};
use anyhow::Result;

/// JIT backend — compiles functions to native code at runtime.
///
/// Planned implementation: Cranelift (via `cranelift-jit` crate) or LLVM (via `inkwell`).
pub fn run(_module: &Module, _func: &Function) -> Result<()> {
    anyhow::bail!("JIT backend not yet implemented")
}
