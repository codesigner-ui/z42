use anyhow::{Context, Result};
use clap::{Parser, ValueEnum};
use std::path::PathBuf;

#[derive(Parser)]
#[command(name = "z42vm", about = "z42 Virtual Machine", version)]
struct Cli {
    /// Bytecode file to execute.
    /// Accepted formats: .zbc (single-file), .zpkg (project package)
    file: String,

    /// Execution mode override (default: use annotation in bytecode)
    #[arg(long, value_enum)]
    mode: Option<ExecMode>,

    /// Enable verbose tracing
    #[arg(short, long)]
    verbose: bool,
}

#[derive(Clone, ValueEnum)]
enum ExecMode {
    Interp,
    Jit,
    Aot,
}

/// Locate the stdlib libs/ directory using three search paths (in priority order):
///   1. $Z42_LIBS environment variable
///   2. <binary-dir>/../libs/   (adjacent to installed binary)
///   3. <cwd>/artifacts/z42/libs/  (development: `cargo run` from project root)
fn resolve_libs_dir() -> Option<PathBuf> {
    // 1. $Z42_LIBS
    if let Ok(v) = std::env::var("Z42_LIBS") {
        let p = PathBuf::from(v);
        if p.is_dir() {
            return Some(p);
        }
    }
    // 2. <binary-dir>/../libs/
    if let Ok(exe) = std::env::current_exe() {
        if let Some(bin_dir) = exe.parent() {
            let p = bin_dir.parent().unwrap_or(bin_dir).join("libs");
            if p.is_dir() {
                return Some(p);
            }
        }
    }
    // 3. <cwd>/artifacts/z42/libs/
    if let Ok(cwd) = std::env::current_dir() {
        let p = cwd.join("artifacts/z42/libs");
        if p.is_dir() {
            return Some(p);
        }
    }
    None
}

/// Log discovered stdlib modules in libs_dir (verbose mode only).
fn log_libs(libs_dir: &PathBuf) {
    tracing::info!("libs dir: {}", libs_dir.display());
    match std::fs::read_dir(libs_dir) {
        Ok(entries) => {
            let mut found = Vec::new();
            for entry in entries.flatten() {
                let path = entry.path();
                if let Some(ext) = path.extension().and_then(|e| e.to_str()) {
                    if ext == "zpkg" || ext == "zbc" {
                        if let Some(name) = path.file_name().and_then(|n| n.to_str()) {
                            found.push(name.to_owned());
                        }
                    }
                }
            }
            found.sort();
            for name in &found {
                tracing::info!("  stdlib module: {name}");
            }
            if found.is_empty() {
                tracing::info!("  (no .zbc/.zpkg files found — stdlib not yet compiled)");
            }
        }
        Err(e) => tracing::warn!("cannot read libs dir: {e}"),
    }
}

/// Resolve module search paths from Z42_PATH, <cwd>/, and <cwd>/modules/.
///
/// Returns a deduplicated list of existing directories in priority order:
///   1. Each entry in `Z42_PATH` (colon-separated on Unix)
///   2. `<cwd>/`
///   3. `<cwd>/modules/`
fn resolve_module_paths() -> Vec<PathBuf> {
    let mut paths: Vec<PathBuf> = Vec::new();

    // 1. Z42_PATH entries
    if let Ok(z42_path) = std::env::var("Z42_PATH") {
        for part in z42_path.split(':') {
            let p = PathBuf::from(part.trim());
            if p.is_dir() && !paths.contains(&p) {
                paths.push(p);
            }
        }
    }

    // 2. <cwd>/
    if let Ok(cwd) = std::env::current_dir() {
        if !paths.contains(&cwd) {
            paths.push(cwd.clone());
        }
        // 3. <cwd>/modules/
        let modules = cwd.join("modules");
        if modules.is_dir() && !paths.contains(&modules) {
            paths.push(modules);
        }
    }

    paths
}

/// Log discovered module paths and .zbc files in verbose mode.
fn log_module_paths(module_paths: &[PathBuf]) {
    for dir in module_paths {
        tracing::info!("module path: {}", dir.display());
        match std::fs::read_dir(dir) {
            Ok(entries) => {
                let mut found = Vec::new();
                for entry in entries.flatten() {
                    let path = entry.path();
                    if path.extension().and_then(|e| e.to_str()) == Some("zbc") {
                        if let Some(name) = path.file_name().and_then(|n| n.to_str()) {
                            found.push(name.to_owned());
                        }
                    }
                }
                found.sort();
                for name in &found {
                    tracing::info!("  module: {name}");
                }
            }
            Err(e) => tracing::warn!("cannot read module path {}: {e}", dir.display()),
        }
    }
}

/// Build the declared-but-not-loaded zpkg candidate set for the lazy loader.
///
/// Sources (in order, deduped by zpkg file name):
///   1. `.zpkg` main artifact's `dependencies` (DEPS section)
///   2. `.zbc`  main artifact's `import_namespaces` — reverse-lookup into
///      `libs_dir` for zpkgs declaring each namespace
///
/// Entries whose file name is already in `initially_loaded` (e.g. `z42.core.zpkg`
/// eager-loaded at startup, or JIT-mode deps already merged) are excluded.
fn build_declared_candidates(
    user_artifact: &z42_vm::metadata::LoadedArtifact,
    libs_dir:      &Option<PathBuf>,
    initially_loaded: &[String],
) -> Vec<(String, z42_vm::metadata::lazy_loader::ZpkgCandidate)> {
    let mut declared: Vec<(String, z42_vm::metadata::lazy_loader::ZpkgCandidate)> = Vec::new();
    let Some(dir) = libs_dir else { return declared };

    let loaded_has = |name: &str| initially_loaded.iter().any(|f| f == name);
    let declared_has = |d: &[(String, _)], name: &str| d.iter().any(|(f, _)| f == name);

    let libs_paths = vec![dir.clone()];

    // .zpkg dependencies (DEPS): file field is authoritative; fall back to
    // the sibling `namespaces` field if the literal filename does not resolve
    // (e.g. GoldenTests writes `${ns}.zpkg` which will not match real stdlib
    // package filenames like `z42.collections.zpkg`).
    for dep in &user_artifact.dependencies {
        if loaded_has(&dep.file) || declared_has(&declared, &dep.file) { continue; }
        if let Ok(cand) = z42_vm::metadata::lazy_loader::ZpkgCandidate::build(dir, &dep.file) {
            declared.push((dep.file.clone(), cand));
            continue;
        }
        // Fallback: reverse lookup by namespaces.
        for ns in &dep.namespaces {
            let Ok(zpkg_paths) = z42_vm::metadata::resolve_namespace(ns, &[], &libs_paths) else {
                continue;
            };
            for zpkg_path in zpkg_paths {
                let Some(file_name) = zpkg_path
                    .file_name()
                    .and_then(|n| n.to_str())
                    .map(str::to_owned)
                else { continue };
                if loaded_has(&file_name) || declared_has(&declared, &file_name) { continue; }
                match z42_vm::metadata::lazy_loader::ZpkgCandidate::build(dir, &file_name) {
                    Ok(cand) => declared.push((file_name, cand)),
                    Err(e)   => tracing::warn!("cannot read zpkg meta `{}`: {e}", file_name),
                }
            }
        }
    }

    // .zbc import_namespaces — reverse lookup
    for ns in &user_artifact.import_namespaces {
        let Ok(zpkg_paths) = z42_vm::metadata::resolve_namespace(ns, &[], &libs_paths) else {
            continue;
        };
        for zpkg_path in zpkg_paths {
            let Some(file_name) = zpkg_path
                .file_name()
                .and_then(|n| n.to_str())
                .map(str::to_owned)
            else { continue };
            if loaded_has(&file_name) { continue; }
            if declared_has(&declared, &file_name) { continue; }
            match z42_vm::metadata::lazy_loader::ZpkgCandidate::build(dir, &file_name) {
                Ok(cand) => declared.push((file_name, cand)),
                Err(e)   => tracing::warn!("cannot read zpkg meta `{}`: {e}", file_name),
            }
        }
    }

    declared
}

fn main() -> Result<()> {
    let cli = Cli::parse();

    if cli.verbose {
        tracing_subscriber::fmt::init();
    }

    // Resolve module search paths (Z42_PATH + cwd + cwd/modules); log only for now.
    let module_paths = resolve_module_paths();
    if cli.verbose {
        log_module_paths(&module_paths);
    }

    tracing::debug!("z42vm loading {}", cli.file);

    // Locate stdlib libs directory.
    let libs_dir = resolve_libs_dir();
    if cli.verbose {
        match &libs_dir {
            Some(dir) => log_libs(dir),
            None => tracing::info!("libs dir: not found (set $Z42_LIBS or run package.sh)"),
        }
    }

    let mut modules: Vec<z42_vm::metadata::Module> = Vec::new();
    // Track canonical paths of loaded artifact files to prevent duplicate loading.
    let mut loaded_paths: std::collections::HashSet<std::path::PathBuf> = std::collections::HashSet::new();
    // Track zpkg file names loaded eagerly at startup (initially_loaded input
    // for the lazy loader — these are excluded from on-demand candidate set).
    let mut initially_loaded_zpkgs: Vec<String> = Vec::new();

    // 5.1b — unconditionally try to load z42.core.zpkg if present.
    if let Some(ref dir) = libs_dir {
        let core_path = dir.join("z42.core.zpkg");
        if core_path.exists() {
            let core_canonical = core_path.canonicalize().unwrap_or(core_path.clone());
            let core_str = core_path.to_string_lossy().into_owned();
            match z42_vm::metadata::load_artifact(&core_str) {
                Ok(a) => {
                    tracing::debug!("loaded stdlib z42.core from {core_str}");
                    modules.push(a.module);
                    loaded_paths.insert(core_canonical);
                    initially_loaded_zpkgs.push("z42.core.zpkg".to_string());
                }
                Err(e) => tracing::warn!("failed to load z42.core: {e}"),
            }
        } else {
            tracing::debug!("z42.core.zpkg not found in {}", dir.display());
        }
    }

    // 5.1c — load the user artifact.
    let user_artifact = z42_vm::metadata::load_artifact(&cli.file)?;

    // 5.1d — dependency loading strategy:
    //   Interp mode → pure lazy. Zpkgs are loaded on demand when the
    //     interpreter encounters a Call to an unresolved function
    //     (see interp/exec_instr.rs + metadata/lazy_loader.rs).
    //   JIT/AOT mode → eager. JIT requires all callee functions to be
    //     pre-compiled, so we pre-load all declared deps at startup.
    let is_eager = matches!(cli.mode, Some(ExecMode::Jit) | Some(ExecMode::Aot));
    if is_eager {
        // Eager: load all declared dependencies (DEPS) and import namespaces.
        for dep in &user_artifact.dependencies {
            if let Some(ref dir) = libs_dir {
                let dep_path = dir.join(&dep.file);
                if dep_path.exists() {
                    let dep_str = dep_path.to_string_lossy().into_owned();
                    if let Ok(a) = z42_vm::metadata::load_artifact(&dep_str) {
                        modules.push(a.module);
                        let canonical = dep_path.canonicalize().unwrap_or(dep_path.clone());
                        loaded_paths.insert(canonical);
                        initially_loaded_zpkgs.push(dep.file.clone());
                    }
                }
            }
        }
        for ns in &user_artifact.import_namespaces {
            if let Some(ref dir) = libs_dir {
                let libs_paths = vec![dir.clone()];
                let Ok(zpkg_paths) = z42_vm::metadata::resolve_namespace(ns, &[], &libs_paths) else { continue };
                for zpkg_path in zpkg_paths {
                    let canonical = zpkg_path.canonicalize().unwrap_or(zpkg_path.clone());
                    if loaded_paths.contains(&canonical) { continue; }
                    let zpkg_str = zpkg_path.to_string_lossy().into_owned();
                    if let Ok(a) = z42_vm::metadata::load_artifact(&zpkg_str) {
                        modules.push(a.module);
                        loaded_paths.insert(canonical);
                        if let Some(name) = zpkg_path.file_name().and_then(|n| n.to_str()) {
                            initially_loaded_zpkgs.push(name.to_string());
                        }
                    }
                }
            }
        }
    }

    // Build declared-but-not-loaded zpkg candidate set for the lazy loader,
    // BEFORE moving `user_artifact.module` into `modules` (partial-move).
    let declared_candidates = build_declared_candidates(
        &user_artifact,
        &libs_dir,
        &initially_loaded_zpkgs,
    );

    // 5.1e — push user module last, then merge everything.
    // Preserve the user module's name so entry-point lookup resolves correctly
    // (merge_modules uses the first module's name, which would be z42.core otherwise).
    let entry_hint = user_artifact.entry_hint.clone();
    let user_module_name = user_artifact.module.name.clone();
    modules.push(user_artifact.module);

    let final_module = if modules.len() == 1 {
        modules.into_iter().next().unwrap()
    } else {
        let mut m = z42_vm::metadata::merge_modules(modules)
            .with_context(|| format!("merging modules for `{}`", cli.file))?;
        m.name = user_module_name;
        z42_vm::metadata::loader::build_type_registry(&mut m);
        z42_vm::metadata::loader::verify_constraints(&m)
            .with_context(|| format!("constraint verification failed for `{}`", cli.file))?;
        z42_vm::metadata::loader::build_block_indices(&mut m);
        z42_vm::metadata::loader::build_func_index(&mut m);
        m
    };

    // Construct the VmContext (consolidate-vm-state, 2026-04-28). The ctx
    // owns static-fields / pending-exception / lazy_loader; previously these
    // lived in thread_local slots scattered across interp/ and jit/.
    //
    // Lazy-loaded zpkgs will have their ConstStr indices offset past this
    // module's string-pool length. In interp mode `declared_candidates`
    // drives on-demand loading; in JIT mode deps are already merged into
    // `modules` during 5.1d so `declared` is typically empty and the lazy
    // loader is effectively a no-op.
    let mut ctx = z42_vm::vm_context::VmContext::new();
    ctx.install_lazy_loader_with_deps(
        libs_dir.clone(),
        final_module.string_pool.len(),
        declared_candidates,
        initially_loaded_zpkgs,
    );

    let default_mode = match cli.mode {
        Some(ExecMode::Jit) => z42_vm::metadata::ExecMode::Jit,
        Some(ExecMode::Aot) => z42_vm::metadata::ExecMode::Aot,
        _                   => z42_vm::metadata::ExecMode::Interp,
    };

    let vm = z42_vm::vm::Vm::new(final_module, default_mode);
    vm.run(&mut ctx, entry_hint.as_deref())
}
