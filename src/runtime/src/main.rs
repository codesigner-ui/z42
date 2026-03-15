use anyhow::{Context, Result};
use clap::{Parser, ValueEnum};

#[derive(Parser)]
#[command(name = "z42vm", about = "z42 Virtual Machine", version)]
struct Cli {
    /// Bytecode file to execute (.z42ir.json for Phase 1, .z42bc for Phase 2)
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

    let json_text = std::fs::read_to_string(&cli.file)
        .with_context(|| format!("cannot read `{}`", cli.file))?;

    let module: z42_vm::bytecode::Module = serde_json::from_str(&json_text)
        .with_context(|| format!("cannot parse bytecode JSON in `{}`", cli.file))?;

    let default_mode = match cli.mode {
        Some(ExecMode::Jit) => z42_vm::types::ExecMode::Jit,
        Some(ExecMode::Aot) => z42_vm::types::ExecMode::Aot,
        _                   => z42_vm::types::ExecMode::Interp,
    };

    let vm = z42_vm::vm::Vm::new(module, default_mode);
    vm.run()
}
