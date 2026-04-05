use anyhow::Result;
use clap::{Parser, ValueEnum};
use std::path::PathBuf;

#[derive(Parser)]
#[command(name = "z42vm", about = "z42 Virtual Machine", version)]
struct Cli {
    /// Bytecode file to execute.
    /// Accepted formats: .z42ir.json (debug IR), .zbc, .zpkg
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

    // Locate stdlib libs directory (log only; actual loading deferred to M7).
    match resolve_libs_dir() {
        Some(dir) => log_libs(&dir),
        None => tracing::info!("libs dir: not found (set $Z42_LIBS or run package.sh)"),
    }

    tracing::debug!("z42vm loading {}", cli.file);

    // Load and merge the artifact (format detected by extension).
    let artifact = z42_vm::metadata::load_artifact(&cli.file)?;

    let default_mode = match cli.mode {
        Some(ExecMode::Jit) => z42_vm::types::ExecMode::Jit,
        Some(ExecMode::Aot) => z42_vm::types::ExecMode::Aot,
        _                   => z42_vm::types::ExecMode::Interp,
    };

    let vm = z42_vm::vm::Vm::new(artifact.module, default_mode);
    vm.run(artifact.entry_hint.as_deref())
}
