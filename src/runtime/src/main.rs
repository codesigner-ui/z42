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

fn main() -> Result<()> {
    let cli = Cli::parse();

    if cli.verbose {
        tracing_subscriber::fmt::init();
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
