use clap::{Parser, ValueEnum};

#[derive(Parser)]
#[command(name = "z42vm", about = "z42 Virtual Machine", version)]
struct Cli {
    /// Bytecode file to execute (.z42bc)
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

fn main() -> anyhow::Result<()> {
    let cli = Cli::parse();

    tracing_subscriber::fmt::init();

    tracing::info!("z42vm starting, file={}", cli.file);

    // TODO: load bytecode and dispatch to VM
    println!("z42vm — not yet implemented. file={}", cli.file);

    Ok(())
}
