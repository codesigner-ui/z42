//! Output formatters: pretty (TTY) / TAP 13 / JSON.
//!
//! Phase: rewrite-z42-test-runner-compile-time S1 (2026-05-10) — extracted
//! from monolithic `main.rs`.

pub mod pretty;
pub mod tap;
pub mod json;

use clap::ValueEnum;

#[derive(Copy, Clone, Debug, PartialEq, Eq, ValueEnum)]
#[clap(rename_all = "lower")]
pub enum Format {
    /// Human-friendly TTY output (colored).
    Pretty,
    /// TAP 13 (`testanything.org`) — perl/Rust-style protocol consumed by CI tooling.
    Tap,
    /// Self-describing JSON document (see `docs/design/testing.md` for schema).
    Json,
}

pub fn resolve_format(explicit: Option<Format>) -> Format {
    use std::io::IsTerminal;
    explicit.unwrap_or_else(|| {
        if std::io::stdout().is_terminal() { Format::Pretty } else { Format::Tap }
    })
}
