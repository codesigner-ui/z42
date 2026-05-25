use anyhow::{Context, Result};
use clap::{Parser, ValueEnum};
use std::path::PathBuf;

#[derive(Parser)]
#[command(name = "z42vm", about = "z42 Virtual Machine", version)]
struct Cli {
    /// Bytecode file to execute.
    /// Accepted formats: .zbc (single-file), .zpkg (project package).
    /// Optional only when `--info` is set.
    file: Option<String>,

    /// Optional entry-function override (positional). When omitted the VM
    /// reads the `Entry` baked into the zpkg by `z42c build` (which itself
    /// auto-detects `Main()` at compile time — see auto-detect-main spec).
    /// Bare `.zbc` files without zpkg metadata REQUIRE this positional
    /// argument; no silent fallback. **z42-test-runner** is the main
    /// consumer: it forks `z42vm <file> <test_method>` per `[Test]`
    /// discovered via TIDX.
    entry: Option<String>,

    /// Execution mode override (default: use annotation in bytecode)
    #[arg(long, value_enum)]
    mode: Option<ExecMode>,

    /// Enable verbose tracing
    #[arg(short, long)]
    verbose: bool,

    /// Print runtime build info (version / target / build profile / enabled features /
    /// exec modes / libs dir / Z42_PATH) and exit. Useful for bug reports and CI
    /// preflight. docs/review.md Part 4 D5 (2026-05-25).
    #[arg(long)]
    info: bool,

    /// Print runtime counter snapshot (builtin_calls / native_calls /
    /// jit_methods_compiled / exceptions_thrown / etc.) to stderr after
    /// the script exits cleanly. docs/review.md Part 4 D6 (2026-05-26).
    #[arg(long)]
    print_stats_on_exit: bool,
}
// 2026-05-11 retire-z-codes: `--explain` / `--list-errors` were removed
// alongside the Rust-side `diagnostics` catalog. Use `z42c explain E####`
// for compile-time codes; runtime errors are typed z42 exceptions now.

// 2026-05-07 add-runtime-feature-flags (P4.1): variants are feature-gated so
// `--help` only advertises modes the binary can actually run, and clap rejects
// unsupported `--mode jit` requests with a friendly enum-list error.
#[derive(Clone, ValueEnum)]
enum ExecMode {
    Interp,
    #[cfg(feature = "jit")]
    Jit,
    #[cfg(feature = "aot")]
    Aot,
}

/// Locate the stdlib libs/ directory.
///
/// Search order (redesign-artifact-layout, 2026-05-12):
///   1. `$Z42_LIBS`                                         — env override
///   2. `<binary-dir>/../libs/`                             — packages/<pkg>/libs/ adjacent
///   3. `<cwd>/artifacts/build/libs/release/`               — dev flat view (build-stdlib.sh)
///   4. `<cwd>/artifacts/build/libs/debug/`                 — dev flat view (debug profile)
///   5. `<cwd>/artifacts/z42/libs/`                         — legacy fallback (pre-2026-05-12)
fn resolve_libs_dir() -> Option<PathBuf> {
    // 1. $Z42_LIBS
    if let Ok(v) = std::env::var("Z42_LIBS") {
        let p = PathBuf::from(v);
        if p.is_dir() {
            return Some(p);
        }
    }
    // 2. <binary-dir>/../libs/  (packages 布局)
    if let Ok(exe) = std::env::current_exe() {
        if let Some(bin_dir) = exe.parent() {
            let p = bin_dir.parent().unwrap_or(bin_dir).join("libs");
            if p.is_dir() {
                return Some(p);
            }
        }
    }
    // 3-4. dev flat view（build-stdlib.sh 产出）
    if let Ok(cwd) = std::env::current_dir() {
        for p in [
            cwd.join("artifacts/build/libs/release"),
            cwd.join("artifacts/build/libs/debug"),
            cwd.join("artifacts/z42/libs"), // legacy fallback
        ] {
            if p.is_dir() {
                return Some(p);
            }
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

/// Initialize the tracing subscriber. Precedence (highest wins):
///   1. `cfg.log_filter` (sourced from `Z42_LOG` env var via `RuntimeConfig`)
///      — `tracing-subscriber` directive syntax
///      (e.g. `Z42_LOG=z42::jit=debug,z42::gc=trace,z42=warn`)
///   2. `--verbose` CLI flag — defaults to `z42=info`
///   3. Otherwise: `z42=warn` (errors + warnings only; quiet boot)
///
/// docs/review.md Part 4 D2 (2026-05-25) + D1 RuntimeConfig migration
/// (2026-05-26): env var consumed via `RuntimeConfig` not inline read.
fn init_tracing(verbose: bool, cfg: &z42::config::RuntimeConfig) {
    use tracing_subscriber::EnvFilter;

    let filter = match cfg.log_filter.as_deref() {
        Some(s) => EnvFilter::try_new(s)
            .unwrap_or_else(|_| EnvFilter::new(default_filter(verbose))),
        None => EnvFilter::new(default_filter(verbose)),
    };
    let _ = tracing_subscriber::fmt()
        .with_env_filter(filter)
        .with_writer(std::io::stderr)
        .try_init();
}

fn default_filter(verbose: bool) -> &'static str {
    if verbose { "z42=info" } else { "z42=warn" }
}

/// Install a custom panic hook that prints VM context + Rust backtrace on
/// internal panic. When `Z42_CRASH_DIR` env var is set and writable, also
/// writes the report to `<dir>/z42vm-crash-<unix_ts_ns>.txt` for offline
/// post-mortem. docs/review.md Part 4 D4 — Phase 1 (2026-05-25).
///
/// Phase 1 covers Rust `panic!()` / unwrap / index OOB / assertion failures.
/// Phase 2 (OS signal handler for SIGSEGV / SIGABRT) is a separate spec —
/// needs the `signal-hook` crate and async-signal-safe primitives.
///
/// Hook composes (not replaces) the default — calls default print first,
/// then appends z42-specific context, then aborts to preserve "panic = bug,
/// can't be caught" semantics.
fn install_panic_hook() {
    let default = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        default(info);

        let mut report = String::new();
        report.push_str("\n=== z42vm internal panic ===\n");
        report.push_str(&format!("z42vm version: {}\n", env!("CARGO_PKG_VERSION")));
        report.push_str(&format!("target: {}/{}\n", std::env::consts::OS, std::env::consts::ARCH));
        report.push_str(&format!("build profile: {}\n",
            if cfg!(debug_assertions) { "debug" } else { "release" }));

        if let Some(loc) = info.location() {
            report.push_str(&format!("panic location: {}:{}:{}\n", loc.file(), loc.line(), loc.column()));
        }

        let payload = info.payload();
        let msg: &str = payload.downcast_ref::<&str>().copied()
            .or_else(|| payload.downcast_ref::<String>().map(String::as_str))
            .unwrap_or("(non-string payload)");
        report.push_str(&format!("payload: {msg}\n"));

        // Rust backtrace — env var `RUST_BACKTRACE=1` controls capture
        let bt = std::backtrace::Backtrace::capture();
        report.push_str(&format!("rust backtrace:\n{bt}\n"));

        report.push_str("(z42 call stack capture pending — Part 4 D4 Phase 2)\n");
        report.push_str("============================\n");

        // Always print to stderr
        eprint!("{report}");

        // Optionally persist to Z42_CRASH_DIR for offline analysis
        if let Ok(dir) = std::env::var("Z42_CRASH_DIR") {
            let dir = std::path::PathBuf::from(dir);
            // Best-effort: create dir, write file, swallow errors (already panicking).
            let _ = std::fs::create_dir_all(&dir);
            let ts_ns = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0);
            let path = dir.join(format!("z42vm-crash-{ts_ns}.txt"));
            if let Err(e) = std::fs::write(&path, &report) {
                eprintln!("[panic hook] failed to write crash report to {}: {e}", path.display());
            } else {
                eprintln!("[panic hook] crash report written to {}", path.display());
            }
        }
    }));
}

/// Print runtime build information to stdout. Triggered by `--info`.
/// Output is intentionally human-readable + grep-friendly (one `key: value`
/// per line). docs/review.md Part 4 D5 (2026-05-25) + D1 RuntimeConfig
/// migration (2026-05-26): enumerates `config::KNOWN_KNOBS` so adding a
/// new knob is one table edit instead of also updating this function.
fn print_build_info() {
    use z42::config::KNOWN_KNOBS;

    println!("z42vm {}", env!("CARGO_PKG_VERSION"));
    println!("target: {}", std::env::consts::OS);
    println!("arch: {}", std::env::consts::ARCH);
    println!("build profile: {}", if cfg!(debug_assertions) { "debug" } else { "release" });

    // Enabled feature flags
    let mut features: Vec<&str> = Vec::new();
    #[cfg(feature = "jit")]            features.push("jit");
    #[cfg(feature = "aot")]            features.push("aot");
    #[cfg(feature = "native-interop")] features.push("native-interop");
    println!("features: {}", if features.is_empty() { "(none)".to_string() } else { features.join(", ") });

    // Exec modes actually available (function of feature flags)
    let mut modes: Vec<&str> = vec!["interp"];
    #[cfg(feature = "jit")] modes.push("jit");
    #[cfg(feature = "aot")] modes.push("aot");
    println!("exec modes: {}", modes.join(", "));

    // Runtime knobs — enumerate from KNOWN_KNOBS so this stays automatically
    // in sync as new env vars get registered. Read raw env directly (not via
    // RuntimeConfig.from_env) so subsystem-local knobs surface too.
    println!("--- runtime knobs ({}) ---", KNOWN_KNOBS.len());
    for knob in KNOWN_KNOBS {
        match std::env::var(knob.name) {
            Ok(v) if !v.trim().is_empty() => println!("{}: {v}", knob.name),
            _                              => println!("{}: ({})", knob.name, knob.default_hint),
        }
    }
    println!("---");

    // Effective libs dir lookup result.
    match resolve_libs_dir() {
        Some(dir) => println!("libs dir: {}", dir.display()),
        None => println!("libs dir: (not found — run ./scripts/build-stdlib.sh or set Z42_LIBS)"),
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
    user_artifact: &z42::metadata::LoadedArtifact,
    libs_dir:      &Option<PathBuf>,
    initially_loaded: &[String],
) -> Vec<(String, z42::metadata::lazy_loader::ZpkgCandidate)> {
    let mut declared: Vec<(String, z42::metadata::lazy_loader::ZpkgCandidate)> = Vec::new();
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
        if let Ok(cand) = z42::metadata::lazy_loader::ZpkgCandidate::build(dir, &dep.file) {
            declared.push((dep.file.clone(), cand));
            continue;
        }
        // Fallback: reverse lookup by namespaces.
        for ns in &dep.namespaces {
            let Ok(zpkg_paths) = z42::metadata::resolve_namespace(ns, &[], &libs_paths) else {
                continue;
            };
            for zpkg_path in zpkg_paths {
                let Some(file_name) = zpkg_path
                    .file_name()
                    .and_then(|n| n.to_str())
                    .map(str::to_owned)
                else { continue };
                if loaded_has(&file_name) || declared_has(&declared, &file_name) { continue; }
                match z42::metadata::lazy_loader::ZpkgCandidate::build(dir, &file_name) {
                    Ok(cand) => declared.push((file_name, cand)),
                    Err(e)   => tracing::warn!("cannot read zpkg meta `{}`: {e}", file_name),
                }
            }
        }
    }

    // .zbc import_namespaces — reverse lookup
    for ns in &user_artifact.import_namespaces {
        let Ok(zpkg_paths) = z42::metadata::resolve_namespace(ns, &[], &libs_paths) else {
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
            match z42::metadata::lazy_loader::ZpkgCandidate::build(dir, &file_name) {
                Ok(cand) => declared.push((file_name, cand)),
                Err(e)   => tracing::warn!("cannot read zpkg meta `{}`: {e}", file_name),
            }
        }
    }

    declared
}

fn main() -> Result<()> {
    let cli = Cli::parse();

    // Centralized runtime config — single source of truth for Z42_* env vars
    // consumed at boot (docs/review.md Part 4 D1, 2026-05-26). Subsystem
    // OnceLock-cached env reads (Z42_GC_* / Z42_NATIVE_PATH / ...) stay
    // inline until their Phase 2 migration.
    let cfg = z42::config::RuntimeConfig::from_env();

    init_tracing(cli.verbose, &cfg);
    install_panic_hook();
    #[cfg(unix)]
    z42::signal_handler::install();

    // --info: print build info to stdout and exit before doing any module loading.
    if cli.info {
        print_build_info();
        return Ok(());
    }

    // Resolve module search paths (Z42_PATH + cwd + cwd/modules); log only for now.
    let module_paths = resolve_module_paths();
    if cli.verbose {
        log_module_paths(&module_paths);
    }

    // `file` is required when not in --info mode. clap can't express "required
    // unless --info" cleanly, so enforce it here.
    let file = cli.file.as_deref()
        .ok_or_else(|| anyhow::anyhow!(
            "missing required argument <FILE> (or pass --info to print build info)"))?;
    tracing::debug!("z42vm loading {}", file);

    // Locate stdlib libs directory.
    let libs_dir = resolve_libs_dir();
    if cli.verbose {
        match &libs_dir {
            Some(dir) => log_libs(dir),
            None => tracing::info!("libs dir: not found (set $Z42_LIBS or run package.sh)"),
        }
    }

    let mut modules: Vec<z42::metadata::Module> = Vec::new();
    // Track canonical paths of loaded artifact files to prevent duplicate loading.
    let mut loaded_paths: std::collections::HashSet<std::path::PathBuf> = std::collections::HashSet::new();
    // Track zpkg file names loaded eagerly at startup (initially_loaded input
    // for the lazy loader — these are excluded from on-demand candidate set).
    let mut initially_loaded_zpkgs: Vec<String> = Vec::new();

    // add-runtime-observer (2026-05-26): collect (name, byte_size) for every
    // module loaded during boot, then replay-emit as `ModuleLoaded` events
    // AFTER VmContext::with_module installs the observer registry. We can't
    // emit at load time because the registry doesn't exist until VmCore is
    // built. Phase 2: lazy_loader will emit synchronously per on-demand load.
    let mut loaded_for_replay: Vec<(String, Option<u64>)> = Vec::new();

    // 5.1b — unconditionally try to load z42.core.zpkg if present.
    if let Some(ref dir) = libs_dir {
        let core_path = dir.join("z42.core.zpkg");
        if core_path.exists() {
            let core_canonical = core_path.canonicalize().unwrap_or(core_path.clone());
            let core_str = core_path.to_string_lossy().into_owned();
            match z42::metadata::load_artifact(&core_str) {
                Ok(a) => {
                    tracing::debug!("loaded stdlib z42.core from {core_str}");
                    let byte_size = std::fs::metadata(&core_path).ok().map(|m| m.len());
                    loaded_for_replay.push(("z42.core.zpkg".to_string(), byte_size));
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
    let user_artifact = z42::metadata::load_artifact(file)?;
    // add-runtime-observer: record user artifact for ModuleLoaded replay.
    {
        let byte_size = std::fs::metadata(file).ok().map(|m| m.len());
        loaded_for_replay.push((file.to_string(), byte_size));
    }

    // 5.1d — dependency loading strategy:
    //   Interp mode → pure lazy. Zpkgs are loaded on demand when the
    //     interpreter encounters a Call to an unresolved function
    //     (see interp/exec_instr.rs + metadata/lazy_loader.rs).
    //   JIT/AOT mode → eager. JIT requires all callee functions to be
    //     pre-compiled, so we pre-load all declared deps at startup.
    // P4.1: branches involving feature-gated variants must also be gated, otherwise
    // the match arm references a non-existent enum constructor when the feature is off.
    let is_eager = match cli.mode {
        #[cfg(feature = "jit")]
        Some(ExecMode::Jit) => true,
        #[cfg(feature = "aot")]
        Some(ExecMode::Aot) => true,
        _ => false,
    };
    if is_eager {
        // Eager: load all declared dependencies (DEPS) and import namespaces.
        for dep in &user_artifact.dependencies {
            if let Some(ref dir) = libs_dir {
                let dep_path = dir.join(&dep.file);
                if dep_path.exists() {
                    let dep_str = dep_path.to_string_lossy().into_owned();
                    if let Ok(a) = z42::metadata::load_artifact(&dep_str) {
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
                let Ok(zpkg_paths) = z42::metadata::resolve_namespace(ns, &[], &libs_paths) else { continue };
                for zpkg_path in zpkg_paths {
                    let canonical = zpkg_path.canonicalize().unwrap_or(zpkg_path.clone());
                    if loaded_paths.contains(&canonical) { continue; }
                    let zpkg_str = zpkg_path.to_string_lossy().into_owned();
                    if let Ok(a) = z42::metadata::load_artifact(&zpkg_str) {
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
        let mut m = z42::metadata::merge_modules(modules)
            .with_context(|| format!("merging modules for `{}`", file))?;
        m.name = user_module_name;
        z42::metadata::loader::build_type_registry(&mut m);
        z42::metadata::loader::verify_constraints(&m)
            .with_context(|| format!("constraint verification failed for `{}`", file))?;
        z42::metadata::loader::build_block_indices(&mut m);
        z42::metadata::loader::build_func_index(&mut m);
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
    // add-threading-stdlib (2026-05-20): module moves into VmCore (shared
    // across threads); Vm becomes a thin run-config wrapper.
    let string_pool_len = final_module.string_pool.len();
    let ctx = z42::vm_context::VmContext::with_module(final_module);
    ctx.install_lazy_loader_with_deps(
        libs_dir.clone(),
        string_pool_len,
        declared_candidates,
        initially_loaded_zpkgs,
    );
    // fix-cross-pkg-subclass-fields (2026-05-14): seed lazy loader with merged
    // module's TypeDescs so cross-zpkg base classes are visible to the fixup
    // pass when a subclass-only zpkg is lazy-loaded later.
    let type_registry = ctx.module().unwrap().type_registry.clone();
    ctx.seed_lazy_loader_types(&type_registry);

    // add-runtime-observer (2026-05-26): replay-emit ModuleLoaded for every
    // module loaded during boot. Empty registry = no-op. Once embedders
    // install observers (e.g. via z42.embedding API), they'll see boot loads
    // as if they had subscribed before startup.
    for (name, byte_size) in loaded_for_replay.drain(..) {
        ctx.fire_runtime_event(&z42::observer::RuntimeEvent::ModuleLoaded {
            name,
            byte_size,
        });
    }

    let default_mode = match cli.mode {
        #[cfg(feature = "jit")]
        Some(ExecMode::Jit) => z42::metadata::ExecMode::Jit,
        #[cfg(feature = "aot")]
        Some(ExecMode::Aot) => z42::metadata::ExecMode::Aot,
        _                   => z42::metadata::ExecMode::Interp,
    };

    let vm = z42::vm::Vm::new(default_mode);
    // CLI positional `entry` overrides any artifact-supplied entry hint.
    let effective_entry = cli.entry.as_deref().or(entry_hint.as_deref());
    let result = vm.run(&*ctx, effective_entry);

    // --print-stats-on-exit (docs/review.md Part 4 D6, 2026-05-26):
    // snapshot counters AFTER vm.run returns. On error (Err return) we
    // still print — partial run still has counter activity. Counters are
    // observation-only so even a failed run's counts are valid.
    if cli.print_stats_on_exit {
        let snap = ctx.counters().snapshot();
        eprintln!("{snap}");
    }

    result
}
