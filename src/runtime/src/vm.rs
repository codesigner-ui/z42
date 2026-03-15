use crate::bytecode::Module;
use crate::types::ExecMode;
use anyhow::Result;

/// The top-level VM — loads a module and dispatches to the appropriate backend.
pub struct Vm {
    pub module: Module,
    pub default_mode: ExecMode,
}

impl Vm {
    pub fn new(module: Module, default_mode: ExecMode) -> Self {
        Vm { module, default_mode }
    }

    /// Execute the module's entry point (`main`).
    pub fn run(&self) -> Result<()> {
        let main = self
            .module
            .functions
            .iter()
            .find(|f| f.name == "main")
            .ok_or_else(|| anyhow::anyhow!("no `main` function found"))?;

        let mode = if main.exec_mode == ExecMode::Interp {
            self.default_mode
        } else {
            main.exec_mode
        };

        match mode {
            ExecMode::Interp => crate::interp::run(&self.module, main),
            ExecMode::Jit => crate::jit::run(&self.module, main),
            ExecMode::Aot => crate::aot::run(&self.module, main),
        }
    }
}
