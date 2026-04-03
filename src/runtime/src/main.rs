use anyhow::Result;
use clap::{Parser, ValueEnum};

#[derive(Parser)]
#[command(name = "z42vm", about = "z42 Virtual Machine", version)]
struct Cli {
    /// Bytecode file to execute.
    /// Accepted formats: .z42ir.json (debug IR), .zbc, .zmod, .zbin
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

fn main() -> Result<()> {
    let cli = Cli::parse();

    if cli.verbose {
        tracing_subscriber::fmt::init();
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
