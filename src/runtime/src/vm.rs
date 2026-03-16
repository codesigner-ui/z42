use crate::bytecode::Module;
use crate::types::ExecMode;
use anyhow::{bail, Result};

/// Top-level VM: loads a module and dispatches to the appropriate backend.
pub struct Vm {
    pub module: Module,
    pub default_mode: ExecMode,
}

impl Vm {
    pub fn new(module: Module, default_mode: ExecMode) -> Self {
        Vm { module, default_mode }
    }

    /// Execute the module's entry point — looks for "Main" (C# convention) or "main".
    pub fn run(&self) -> Result<()> {
        let entry_name = ["Main", "main"]
            .iter()
            .find(|&&n| self.module.functions.iter().any(|f| f.name == n))
            .copied()
            .ok_or_else(|| anyhow::anyhow!(
                "no entry point (`Main` or `main`) found in module `{}`",
                self.module.name
            ))?;

        let entry = self
            .module
            .functions
            .iter()
            .find(|f| f.name == entry_name)
            .ok_or_else(|| anyhow::anyhow!("entry `{}` disappeared — this is a bug", entry_name))?;

        match self.default_mode {
            ExecMode::Interp => crate::interp::run(&self.module, entry, &[]),
            ExecMode::Jit    => bail!("JIT mode not yet implemented"),
            ExecMode::Aot    => bail!("AOT mode not yet implemented"),
        }
    }
}
