use crate::metadata::{ExecMode, Module};
use anyhow::{bail, Result};

/// Top-level VM: holds a merged IR module and dispatches to the appropriate backend.
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
    /// Entry resolution order (first match wins):
    ///   1. `hint`               — explicit name from artifact metadata (.zpkg `entry` field)
    ///   2. `{module.name}.Main`
    ///   3. `{module.name}.main`
    ///   4. `Main`
    ///   5. `main`
    pub fn run(&self, hint: Option<&str>) -> Result<()> {
        let entry_name = self.resolve_entry(hint)?;

        let entry = self
            .module
            .functions
            .iter()
            .find(|f| f.name == entry_name)
            .ok_or_else(|| anyhow::anyhow!("entry `{}` disappeared — this is a bug", entry_name))?;

        match self.default_mode {
            ExecMode::Interp => crate::interp::run_with_static_init(&self.module, entry),
            ExecMode::Jit    => crate::jit::run(&self.module, &entry_name),
            ExecMode::Aot    => bail!("AOT mode not yet implemented"),
        }
    }

    fn resolve_entry(&self, hint: Option<&str>) -> Result<String> {
        let ns = &self.module.name;
        let qualified_main    = format!("{}.Main", ns);
        let qualified_main_lc = format!("{}.main", ns);

        // Build candidate list; hint is tried first if present.
        let mut candidates: Vec<&str> = Vec::with_capacity(5);
        if let Some(h) = hint { candidates.push(h); }
        candidates.extend_from_slice(&[&qualified_main, &qualified_main_lc, "Main", "main"]);

        candidates
            .into_iter()
            .find(|&n| self.module.functions.iter().any(|f| f.name == n))
            .map(|s| s.to_owned())
            .ok_or_else(|| {
                anyhow::anyhow!(
                    "no entry point found in module `{ns}` \
                     (tried: {}.Main, {}.main, Main, main)",
                    ns, ns
                )
            })
    }
}
