use crate::bytecode::{Function, Module};
use anyhow::Result;

/// AOT backend — compiles the whole module to a native binary before execution.
///
/// Planned implementation: LLVM IR generation via `inkwell`, then `llc` / `clang` linkage.
pub fn run(_module: &Module, _func: &Function) -> Result<()> {
    anyhow::bail!("AOT backend not yet implemented")
}
