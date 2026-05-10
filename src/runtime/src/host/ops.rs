//! Operational logic for the host C ABI: corelib probe, `.zbc` load,
//! entry resolution, and invocation. Kept distinct from `mod.rs` so the
//! `extern "C"` surface stays a thin dispatcher.
//!
//! Spec: docs/design/runtime/embedding.md §4.4 + docs/spec/archive/2026-05-10-add-embedding-api/design.md.

use std::path::{Path, PathBuf};

use anyhow::{anyhow, bail, Context, Result};

use crate::corelib::io;
use crate::interp;
use crate::metadata::{
    load_artifact, load_artifact_from_bytes, merge_modules, Module, Value,
};
use crate::vm_context::VmContext;

use super::config::ResolvedConfig;
use super::marshal::{value_to_z42_value, z42_value_to_value};
use super::state::{HostCorelib, HostEntry, HostModule};

/// Probe the configured `search_paths` for `z42.core.zpkg`. Records the
/// path; the actual `Module` is re-read from disk on every
/// `build_host_module` call (cheap relative to user-code execution and
/// avoids the `Module` `Clone` requirement, which would otherwise
/// duplicate index tables).
///
/// Returns `Ok(None)` if no path contains the file. User code that
/// references corelib types will then surface a normal "type not found"
/// diagnostic at first reference.
pub(crate) fn probe_corelib(config: &ResolvedConfig) -> Result<Option<HostCorelib>> {
    const CORELIB_FILE: &str = "z42.core.zpkg";
    for raw in &config.search_paths {
        let dir = PathBuf::from(raw);
        let candidate = dir.join(CORELIB_FILE);
        if !candidate.is_file() {
            continue;
        }
        // Eager probe-load to validate the file is parseable; result
        // discarded here. If this fails the user sees a clear
        // initialize-time error instead of a confusing failure on the
        // first load_zbc.
        let path_str = candidate.to_string_lossy().into_owned();
        load_artifact(&path_str)
            .with_context(|| format!("validating corelib at {path_str}"))?;
        return Ok(Some(HostCorelib {
            zpkg_path: candidate,
            initially_loaded: vec![CORELIB_FILE.to_string()],
            libs_dir: dir,
        }));
    }
    Ok(None)
}

/// Build a fully-prepared user `Module` and a fresh `VmContext` to back
/// it. Mirrors `src/runtime/src/main.rs` lines 295–412:
///
/// 1. parse the user `.zbc` from bytes (`load_artifact_from_bytes`)
/// 2. eager-load corelib (`z42.core.zpkg`) if found via `search_paths`
/// 3. for each `import_namespace` in the user module, resolve the
///    matching `.zpkg` files in `libs_dir` and eager-load those too
/// 4. merge all modules and rebuild the type / block / func indices
/// 5. install the lazy loader with an empty `declared_candidates` (the
///    eager merge already pulled in everything Hello.Main needs;
///    multi-zpkg lazy resolution lands in H3)
///
/// Keeping the merge eager — instead of relying on `declared_candidates`
/// — is intentional for H2: the host is single-instance and does the
/// dependency walk once per `load_zbc`, so we trade a small load-time
/// cost for "no surprising lazy lookups during invoke".
pub(crate) fn build_host_module(
    bytes: &[u8],
    corelib: Option<&HostCorelib>,
) -> Result<HostModule> {
    let user_artifact = load_artifact_from_bytes(bytes)
        .context("z42_host_load_zbc: cannot parse bytes as a .zbc / .zpkg artifact")?;

    let user_module_name = user_artifact.module.name.clone();
    let mut modules: Vec<Module> = Vec::with_capacity(4);
    let mut initially_loaded: Vec<String> = Vec::new();
    let libs_dir = corelib.as_ref().map(|c| c.libs_dir.clone());

    // (2) corelib first — main.rs invariant: z42.core is implicit prelude.
    if let Some(c) = corelib {
        let path_str = c.zpkg_path.to_string_lossy().into_owned();
        let corelib_artifact = load_artifact(&path_str)
            .with_context(|| format!("z42_host_load_zbc: re-reading corelib at {path_str}"))?;
        modules.push(corelib_artifact.module);
        initially_loaded.extend(c.initially_loaded.iter().cloned());
    }

    // (3) follow the user .zbc's import_namespaces — same scan as main.rs.
    if let Some(libs) = libs_dir.as_ref() {
        let libs_paths = vec![libs.clone()];
        let mut loaded_paths: std::collections::HashSet<PathBuf> =
            std::collections::HashSet::new();
        if let Some(c) = corelib {
            if let Ok(canonical) = c.zpkg_path.canonicalize() {
                loaded_paths.insert(canonical);
            }
        }
        for ns in &user_artifact.import_namespaces {
            let Ok(zpkg_paths) =
                crate::metadata::resolve_namespace(ns, &[], &libs_paths)
            else {
                continue;
            };
            for zpkg_path in zpkg_paths {
                let canonical = zpkg_path
                    .canonicalize()
                    .unwrap_or_else(|_| zpkg_path.clone());
                if !loaded_paths.insert(canonical) {
                    continue;
                }
                let path_str = zpkg_path.to_string_lossy().into_owned();
                let dep = load_artifact(&path_str).with_context(|| {
                    format!("z42_host_load_zbc: loading import dependency {path_str}")
                })?;
                modules.push(dep.module);
                if let Some(name) = zpkg_path.file_name().and_then(|n| n.to_str()) {
                    initially_loaded.push(name.to_string());
                }
            }
        }
    }

    // (4) push the user module last so merge_modules' name-keep behaviour
    // doesn't accidentally rename it to the first dependency.
    modules.push(user_artifact.module);

    let final_module = if modules.len() == 1 {
        modules.into_iter().next().unwrap()
    } else {
        let mut m = merge_modules(modules)
            .context("z42_host_load_zbc: merging dependencies into user module")?;
        m.name = user_module_name;
        crate::metadata::loader::build_type_registry(&mut m);
        crate::metadata::loader::verify_constraints(&m).with_context(|| {
            format!("z42_host_load_zbc: constraint verification failed for `{}`", m.name)
        })?;
        crate::metadata::loader::build_block_indices(&mut m);
        crate::metadata::loader::build_func_index(&mut m);
        m
    };

    let ctx = VmContext::new();
    ctx.install_lazy_loader_with_deps(
        libs_dir,
        final_module.string_pool.len(),
        Vec::new(),
        initially_loaded,
    );

    Ok(HostModule {
        module: final_module,
        ctx,
    })
}

/// Look up a fully-qualified function name in a host-module's function
/// table. Accepts both the dot form `"namespace.Type.method"` and the
/// `::` form `"namespace.Type::method"` (the spec uses the latter for
/// readability; the bytecode currently records the former).
pub(crate) fn resolve_fqn(module: &Module, fqn: &str) -> Result<usize> {
    if fqn.is_empty() {
        bail!("z42_host_resolve_entry: fqn is empty");
    }
    if let Some(&idx) = module.func_index.get(fqn) {
        return Ok(idx);
    }
    // Tolerate the "ns.Type::method" form by normalising `::` → `.`.
    if fqn.contains("::") {
        let normalised = fqn.replace("::", ".");
        if let Some(&idx) = module.func_index.get(&normalised) {
            return Ok(idx);
        }
    }
    Err(anyhow!(
        "z42_host_resolve_entry: function `{fqn}` not found in module `{}`",
        module.name
    ))
}

/// RAII guard that toggles the per-thread "host sink active" flag.
/// Activated on entry to `z42_host_invoke`, restored on Drop so panics
/// or early returns can't leave a dangling flag.
pub(crate) struct HostSinkGuard {
    prev: bool,
}

impl HostSinkGuard {
    pub(crate) fn enter() -> Self {
        let prev = io::host_sink_set_active(true);
        Self { prev }
    }
}

impl Drop for HostSinkGuard {
    fn drop(&mut self) {
        io::host_sink_set_active(self.prev);
    }
}

/// Invoke an entry point. Marshals args + return value through the
/// `Z42Value` ABI; activates host stdout / stderr dispatch for the
/// duration of the call.
///
/// Errors are returned as `anyhow::Error` so the `extern "C"` boundary
/// can classify them. The caller maps the special prefix
/// `"arg-count-mismatch:"` to `Z42_HOST_ERR_ARG_MISMATCH`; any z42
/// throw escaping the entry surfaces as `Z42_HOST_ERR_VM_EXCEPTION`
/// via `classify_invoke_error`.
pub(crate) fn invoke_impl(
    host_module: &HostModule,
    entry: &HostEntry,
    args_bytes: &[Value],
) -> Result<Option<Value>> {
    let func = host_module
        .module
        .functions
        .get(entry.fn_idx)
        .ok_or_else(|| anyhow!("z42_host_invoke: entry function index out of bounds"))?;

    if args_bytes.len() != func.param_count {
        bail!(
            "arg-count-mismatch: function `{}` expects {} arg(s), got {}",
            func.name,
            func.param_count,
            args_bytes.len()
        );
    }

    let _sink_guard = HostSinkGuard::enter();
    interp::run_returning(&host_module.ctx, &host_module.module, func, args_bytes)
}

/// Marshal a slice of `Z42Value` into runtime `Value`s. Errors mirror
/// `Z42_HOST_ERR_ARG_MISMATCH` semantics (caller maps).
pub(crate) fn marshal_args(args: &[z42_abi::Z42Value]) -> Result<Vec<Value>> {
    args.iter().map(z42_value_to_value).collect()
}

/// Marshal a returned `Option<Value>` back to a `Z42Value` writable to
/// `out_result`. `None` (void return) maps to a NULL-tagged value.
pub(crate) fn marshal_return(value: Option<&Value>) -> Result<z42_abi::Z42Value> {
    value_to_z42_value(value)
}

/// Search-path probe shared with `probe_corelib` for diagnostic logging.
#[allow(dead_code)] // referenced by future H3 multi-zpkg work
pub(crate) fn libs_dir_from_search_paths(config: &ResolvedConfig) -> Option<PathBuf> {
    for raw in &config.search_paths {
        let p = Path::new(raw);
        if p.is_dir() {
            return Some(p.to_path_buf());
        }
    }
    None
}
