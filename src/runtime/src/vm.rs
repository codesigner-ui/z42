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

    /// Execute the module's entry point.
    ///
    /// Lookup order (first match wins):
    ///   1. `{namespace}.Main`
    ///   2. `{namespace}.main`
    ///   3. `Main`
    ///   4. `main`
    pub fn run(&self) -> Result<()> {
        let ns = &self.module.name;
        let qualified_main    = format!("{}.Main", ns);
        let qualified_main_lc = format!("{}.main", ns);
        let candidates: [&str; 4] = [&qualified_main, &qualified_main_lc, "Main", "main"];

        let entry_name = candidates
            .iter()
            .copied()
            .find(|&n| self.module.functions.iter().any(|f| f.name == n))
            .ok_or_else(|| anyhow::anyhow!(
                "no entry point found in module `{}` (tried: {}.Main, {}.main, Main, main)",
                ns, ns, ns
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
