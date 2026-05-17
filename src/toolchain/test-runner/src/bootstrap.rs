//! In-process VM bootstrap — load + merge + lazy_loader setup.
//!
//! Phase: rewrite-z42-test-runner-compile-time S3 (2026-05-10).
//!
//! Replicates the `z42vm` binary bootstrap so test-runner can run [Test]
//! functions in-process via `interp::run`, eliminating subprocess fork
//! overhead and enabling shared-state Setup/Teardown chains.
//!
//! Mirrors `src/runtime/src/main.rs` 5.1b/5.1c/5.1e logic (eager z42.core
//! + lazy declared zpkgs + merge_modules). JIT mode dependencies are not
//! pre-loaded since R3b runner uses Interp by default for predictability.

use anyhow::{Context, Result};
use std::path::PathBuf;

use z42::metadata::{load_artifact, merge_modules, LoadedArtifact, Module, TestEntry};
use z42::vm::Vm;
use z42::vm_context::VmContext;

pub struct LoadedRunner {
    /// Test metadata from the user artifact's TIDX section.
    pub test_index: Vec<TestEntry>,
    /// Function names indexed by user-module method_id (TIDX entries reference
    /// these). Frozen at bootstrap time — the user module is moved into the
    /// merged module for execution but ids would shift; resolving via name
    /// is the stable lookup.
    pub user_func_names: Vec<String>,
    /// Merged module (z42.core + user) ready for execution. Find a test
    /// function by name via `vm.module.functions.iter().find(|f| f.name == ...)`.
    pub vm: Vm,
    pub ctx: VmContext,
}

/// Bootstrap an in-process VM from a single .zbc artifact.
pub fn bootstrap(zbc_path: &str) -> Result<LoadedRunner> {
    let libs_dir = resolve_libs_dir();

    let mut modules: Vec<Module> = Vec::new();
    let mut loaded_paths: std::collections::HashSet<std::path::PathBuf> = std::collections::HashSet::new();
    let mut initially_loaded_zpkgs: Vec<String> = Vec::new();

    // Eagerly load z42.core (mirrors z42vm main.rs 5.1b).
    if let Some(ref dir) = libs_dir {
        let core_path = dir.join("z42.core.zpkg");
        if core_path.exists() {
            let core_canonical = core_path.canonicalize().unwrap_or(core_path.clone());
            let core_str = core_path.to_string_lossy().into_owned();
            if let Ok(a) = load_artifact(&core_str) {
                modules.push(a.module);
                loaded_paths.insert(core_canonical);
                initially_loaded_zpkgs.push("z42.core.zpkg".to_string());
            }
        }
    }

    // Load user artifact.
    let user_artifact = load_artifact(zbc_path)
        .with_context(|| format!("loading artifact `{zbc_path}`"))?;

    // Build declared-but-not-loaded zpkg candidate set for the lazy loader,
    // BEFORE moving user_artifact.module into modules. Mirrors private
    // `build_declared_candidates` in src/runtime/src/main.rs (S5 follow-up
    // could extract to runtime crate as a shared helper).
    let declared_candidates = build_declared_candidates(
        &user_artifact, &libs_dir, &initially_loaded_zpkgs,
    );

    // Save data we need later before partial-moving user_artifact.
    let user_module_name = user_artifact.module.name.clone();
    let test_index = user_artifact.test_index.clone();
    let user_func_names: Vec<String> =
        user_artifact.module.functions.iter().map(|f| f.name.clone()).collect();
    drop(user_artifact.entry_hint);
    drop(user_artifact.dependencies);
    drop(user_artifact.import_namespaces);

    modules.push(user_artifact.module);

    // Merge into a single executable module.
    let final_module = if modules.len() == 1 {
        modules.into_iter().next().unwrap()
    } else {
        let mut m = merge_modules(modules)
            .with_context(|| format!("merging modules for `{zbc_path}`"))?;
        m.name = user_module_name;
        z42::metadata::loader::build_type_registry(&mut m);
        z42::metadata::loader::verify_constraints(&m)
            .with_context(|| format!("constraint verification failed for `{zbc_path}`"))?;
        z42::metadata::loader::build_block_indices(&mut m);
        z42::metadata::loader::build_func_index(&mut m);
        m
    };

    let ctx = VmContext::new();
    ctx.install_lazy_loader_with_deps(
        libs_dir,
        final_module.string_pool.len(),
        declared_candidates,
        initially_loaded_zpkgs,
    );
    // fix-cross-pkg-subclass-fields (2026-05-14): seed lazy loader with the
    // merged module's TypeDescs so cross-zpkg fixup of lazy-loaded subclasses
    // can find their base classes (e.g. ProcessStartException in z42.io
    // inheriting from Std.Exception eagerly-loaded via z42.core).
    ctx.seed_lazy_loader_types(&final_module.type_registry);

    let vm = Vm::new(final_module, z42::metadata::ExecMode::Interp);

    Ok(LoadedRunner { test_index, user_func_names, vm, ctx })
}

/// Build declared-but-not-loaded zpkg candidate list for the lazy loader.
/// Mirrors `src/runtime/src/main.rs::build_declared_candidates` (private there).
fn build_declared_candidates(
    user_artifact: &LoadedArtifact,
    libs_dir:      &Option<PathBuf>,
    initially_loaded: &[String],
) -> Vec<(String, z42::metadata::lazy_loader::ZpkgCandidate)> {
    let mut declared: Vec<(String, z42::metadata::lazy_loader::ZpkgCandidate)> = Vec::new();
    let Some(dir) = libs_dir else { return declared };

    let loaded_has = |name: &str| initially_loaded.iter().any(|f| f == name);
    let declared_has = |d: &[(String, _)], name: &str| d.iter().any(|(f, _)| f == name);

    let libs_paths = vec![dir.clone()];

    for dep in &user_artifact.dependencies {
        if loaded_has(&dep.file) || declared_has(&declared, &dep.file) { continue; }
        if let Ok(cand) = z42::metadata::lazy_loader::ZpkgCandidate::build(dir, &dep.file) {
            declared.push((dep.file.clone(), cand));
            continue;
        }
        for ns in &dep.namespaces {
            let Ok(zpkg_paths) = z42::metadata::resolve_namespace(ns, &[], &libs_paths) else { continue };
            for zpkg_path in zpkg_paths {
                let Some(file_name) = zpkg_path.file_name().and_then(|n| n.to_str()).map(str::to_owned) else { continue };
                if loaded_has(&file_name) || declared_has(&declared, &file_name) { continue; }
                if let Ok(cand) = z42::metadata::lazy_loader::ZpkgCandidate::build(dir, &file_name) {
                    declared.push((file_name, cand));
                }
            }
        }
    }

    for ns in &user_artifact.import_namespaces {
        let Ok(zpkg_paths) = z42::metadata::resolve_namespace(ns, &[], &libs_paths) else { continue };
        for zpkg_path in zpkg_paths {
            let Some(file_name) = zpkg_path.file_name().and_then(|n| n.to_str()).map(str::to_owned) else { continue };
            if loaded_has(&file_name) { continue; }
            if declared_has(&declared, &file_name) { continue; }
            if let Ok(cand) = z42::metadata::lazy_loader::ZpkgCandidate::build(dir, &file_name) {
                declared.push((file_name, cand));
            }
        }
    }

    declared
}

/// Locate the stdlib libs/ directory.
///
/// Search order (mirrors `src/runtime/src/main.rs::resolve_libs_dir`,
/// redesign-artifact-layout 2026-05-12):
///   1. `$Z42_LIBS`                                         — env override
///   2. `<binary-dir>/../libs/`                             — packages/<pkg>/libs/ adjacent
///   3. `<cwd>/artifacts/build/libs/release/`               — dev flat view
///   4. `<cwd>/artifacts/build/libs/debug/`                 — dev flat view (debug profile)
///   5. `<cwd>/artifacts/z42/libs/`                         — legacy fallback
fn resolve_libs_dir() -> Option<PathBuf> {
    if let Ok(v) = std::env::var("Z42_LIBS") {
        let p = PathBuf::from(v);
        if p.is_dir() { return Some(p); }
    }
    if let Ok(exe) = std::env::current_exe() {
        if let Some(bin_dir) = exe.parent() {
            let p = bin_dir.parent().unwrap_or(bin_dir).join("libs");
            if p.is_dir() { return Some(p); }
        }
    }
    if let Ok(cwd) = std::env::current_dir() {
        for p in [
            cwd.join("artifacts/build/libs/release"),
            cwd.join("artifacts/build/libs/debug"),
            cwd.join("artifacts/z42/libs"),
        ] {
            if p.is_dir() { return Some(p); }
        }
    }
    None
}
