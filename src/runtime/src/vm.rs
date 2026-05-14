use crate::metadata::{ExecMode, Module};
use crate::vm_context::VmContext;
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
    /// `ctx` carries the runtime-mutable state (static fields, lazy loader,
    /// pending exception). Caller is responsible for `ctx.install_lazy_loader`
    /// before calling `run` if dependencies need to be lazy-loaded.
    ///
    /// Entry resolution (strict, since 2026-05-14 auto-detect-main +
    /// drop-cli-entry-fallback): `hint` must be `Some(name)`. The compiler
    /// (`PackageCompiler.BuildTarget.AutoDetectEntry`) bakes `Entry` into
    /// every exe zpkg; the artifact loader passes it as `hint`. CLI users
    /// can override with the positional `[entry]` arg on `z42vm`. No
    /// silent fallback chain — missing entry → hard error.
    pub fn run(&self, ctx: &mut VmContext, hint: Option<&str>) -> Result<()> {
        let entry_name = self.resolve_entry(hint)?;

        // 2026-05-02 add-method-group-conversion (D1b): pre-allocate the FuncRef
        // cache slots needed by `LoadFnCached` instructions for this module's
        // global slot range.
        ctx.alloc_func_ref_slots(self.module.func_ref_cache_slots);

        // introduce-method-token Phase 3 (2026-05-08): pre-resolve dispatch
        // tokens for every Function. Idempotent — safe if hot paths run
        // before Phase 4 hookups consume the cache (they fall back to
        // string lookup until Phase 4 lands).
        crate::metadata::resolver::resolve_module(&self.module, ctx);

        let entry = self
            .module
            .functions
            .iter()
            .find(|f| f.name == entry_name)
            .ok_or_else(|| anyhow::anyhow!("entry `{}` disappeared — this is a bug", entry_name))?;

        match self.default_mode {
            ExecMode::Interp => crate::interp::run_with_static_init(ctx, &self.module, entry),
            // 2026-05-07 add-runtime-feature-flags (P4.1): JIT path is feature-gated.
            // When `jit` feature is off, fall through to a friendly bail so old zbc
            // files with `exec_mode = Jit` produce a clear "recompile with --features jit"
            // message instead of an opaque link error.
            #[cfg(feature = "jit")]
            ExecMode::Jit    => crate::jit::run(ctx, &self.module, &entry_name),
            #[cfg(not(feature = "jit"))]
            ExecMode::Jit    => bail!(
                "JIT mode requested but the runtime was built without the `jit` feature. \
                 Recompile with `--features jit` (default), or run the bytecode in `--mode interp`."
            ),
            ExecMode::Aot    => bail!(
                "AOT mode is not implemented yet (planned: roadmap M9 — L3 \
                 phase, LLVM/inkwell). Switch to `--mode interp` or \
                 `--mode jit`."
            ),
        }
    }

    /// Strict entry resolution (no fallback chain). The hint **must** be
    /// supplied — either from the zpkg `Entry` field (baked at compile
    /// time by `PackageCompiler.BuildTarget.AutoDetectEntry`) or from the
    /// CLI positional `[entry]` argument on `z42vm`.
    fn resolve_entry(&self, hint: Option<&str>) -> Result<String> {
        let name = hint.ok_or_else(|| anyhow::anyhow!(
            "no entry point: pass the function name as the second positional \
             argument to `z42vm`, or rebuild with `z42c build` which bakes \
             an entry into the zpkg automatically (define a `Main()` in source)"
        ))?;
        if !self.module.functions.iter().any(|f| f.name == name) {
            bail!("entry function `{}` not found in module `{}`", name, self.module.name);
        }
        Ok(name.to_string())
    }
}
