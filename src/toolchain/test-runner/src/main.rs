//! z42-test-runner — minimal R3 implementation (spec
//! `rewrite-z42-test-runner-compile-time`).
//!
//! 当前 R3 minimal v0.2 scope:
//! - 加载单个 .zbc 文件，从 TIDX section 读 [Test] / [Skip] 信息（in-process）
//! - 每个 `[Test]` **fork 一个 z42vm 子进程** 跑 `z42vm <zbc> --entry <name>`
//! - 通过 stderr 内容 + exit code 分类：
//!     - exit 0                                        → Outcome::Passed
//!     - stderr 含 "Std.SkipSignal"                    → Outcome::Skipped
//!     - stderr 含 "Std.TestFailure"                   → Outcome::Failed (TestFailure)
//!     - 其他 exit ≠ 0                                  → Outcome::Failed (Other Exception)
//! - 子进程方案让我们**复用 z42vm 完整的 stdlib 依赖加载**（z42.core /
//!   z42.test 等），不必在 test-runner 内复刻 lazy loader 逻辑
//! - 进程级隔离自带：测试间无状态泄漏
//!
//! 推迟（受其他 spec 阻塞或未实现）:
//! - Setup / Teardown 调度：当前每个 [Test] 单独子进程，无法跨进程共享 setup
//!   状态。R3 完整版用 z42vm `--pre <setup>` `--post <teardown>` 多 entry。
//! - --filter / --tag / --bench / TAP / JSON formatter — R3 完整版
//! - Bencher 模式 — R2.C (依赖 closure)
//! - [ShouldThrow<E>] / [TestCase(args)] — R4
//!
//! 用法：
//!   z42-test-runner <file.zbc> [--z42vm <path>]

use anyhow::{bail, Context, Result};
use clap::Parser;
use colored::*;
use std::path::PathBuf;
use std::process::Command;
use std::time::Instant;

use z42_vm::metadata::{
    load_artifact, LoadedArtifact, TestEntry, TestEntryKind, TestFlags,
};

#[derive(Parser)]
#[command(name = "z42-test-runner", about = "z42 test runner (R3 minimal v0.2)", version)]
struct Cli {
    /// Path to a .zbc file containing test methods (decorated with z42.test attributes).
    file: PathBuf,

    /// Override z42vm binary path. Default: sibling of test-runner binary, then $PATH.
    #[arg(long)]
    z42vm: Option<PathBuf>,

    /// Force-disable color output (default: auto-detect TTY).
    #[arg(long)]
    no_color: bool,
}

fn main() {
    let cli = Cli::parse();
    if cli.no_color {
        colored::control::set_override(false);
    }

    match run(&cli) {
        Ok(exit_code) => std::process::exit(exit_code),
        Err(e) => {
            eprintln!("{}: {e:#}", "error".red().bold());
            std::process::exit(2);
        }
    }
}

fn run(cli: &Cli) -> Result<i32> {
    let zbc_path = cli.file.to_str().context("file path is not valid UTF-8")?;
    let artifact = load_artifact(zbc_path)
        .with_context(|| format!("loading artifact `{zbc_path}`"))?;

    let report = TestReport::from_artifact(&artifact);
    if report.tests.is_empty() {
        println!("{}", "no tests found (TIDX section empty or absent)".yellow());
        return Ok(3);
    }

    let z42vm = resolve_z42vm(cli.z42vm.as_ref())
        .context("locating z42vm binary (use --z42vm to override)")?;

    print_header(&report, zbc_path);
    let mut summary = RunSummary::default();

    for test in &report.tests {
        let outcome = run_one(&z42vm, zbc_path, test);
        match outcome {
            Outcome::Passed { duration_ms } => {
                summary.passed += 1;
                println!("  {} {}  ({}ms)", "✓".green().bold(), test.method_name, duration_ms);
            }
            Outcome::Skipped { reason } => {
                summary.skipped += 1;
                println!("  {} {}  ({})", "⊘".yellow().bold(), test.method_name, reason);
            }
            Outcome::Failed { reason } => {
                summary.failed += 1;
                println!("  {} {}", "✗".red().bold(), test.method_name);
                for line in reason.lines() {
                    println!("      {}", line.red());
                }
            }
        }
    }

    print_footer(&summary);
    Ok(if summary.failed > 0 { 1 } else { 0 })
}

// ── Discovery ─────────────────────────────────────────────────────────────

#[allow(dead_code)] // method_id reserved for R3 full impl filtering
struct DiscoveredTest<'a> {
    method_id: u32,
    method_name: &'a str,
    flags: TestFlags,
    skip_reason: Option<String>,
}

struct TestReport<'a> {
    tests: Vec<DiscoveredTest<'a>>,
}

impl<'a> TestReport<'a> {
    fn from_artifact(artifact: &'a LoadedArtifact) -> Self {
        let mut tests = Vec::new();
        for entry in &artifact.test_index {
            if entry.kind != TestEntryKind::Test { continue; }
            if entry.flags.contains(TestFlags::IGNORED) {
                // [Ignore] — silently omit per design.
                continue;
            }
            let method = &artifact.module.functions[entry.method_id as usize];
            let skip_reason = if entry.flags.contains(TestFlags::SKIPPED) {
                Some(format_skip_reason(entry))
            } else {
                None
            };
            tests.push(DiscoveredTest {
                method_id: entry.method_id,
                method_name: &method.name,
                flags: entry.flags,
                skip_reason,
            });
        }
        Self { tests }
    }
}

fn format_skip_reason(entry: &TestEntry) -> String {
    let mut parts: Vec<String> = Vec::new();
    if let Some(p) = &entry.skip_platform { parts.push(format!("platform={p}")); }
    if let Some(f) = &entry.skip_feature  { parts.push(format!("feature={f}"));  }
    if let Some(r) = &entry.skip_reason   { parts.push(r.clone()); }
    if parts.is_empty() { "skipped".into() } else { parts.join("; ") }
}

// ── Execution (subprocess to z42vm) ───────────────────────────────────────

enum Outcome {
    Passed { duration_ms: u64 },
    Failed { reason: String },
    Skipped { reason: String },
}

fn run_one(z42vm: &PathBuf, zbc_path: &str, test: &DiscoveredTest) -> Outcome {
    if let Some(reason) = &test.skip_reason {
        return Outcome::Skipped { reason: reason.clone() };
    }

    let start = Instant::now();
    let output = Command::new(z42vm)
        .arg(zbc_path)
        .arg("--entry").arg(test.method_name)
        .output();

    let duration_ms = start.elapsed().as_millis() as u64;

    let output = match output {
        Ok(o) => o,
        Err(e) => return Outcome::Failed {
            reason: format!("failed to spawn z42vm: {e}")
        },
    };

    let stderr = String::from_utf8_lossy(&output.stderr);
    let stdout = String::from_utf8_lossy(&output.stdout);

    if output.status.success() {
        return Outcome::Passed { duration_ms };
    }

    // Classify by stderr content. z42vm prints
    // "Error: uncaught exception: Std.TestFailure{...}" or similar.
    if stderr.contains("Std.SkipSignal") {
        return Outcome::Skipped { reason: extract_exception_msg(&stderr) };
    }
    if stderr.contains("Std.TestFailure") {
        return Outcome::Failed { reason: extract_exception_msg(&stderr) };
    }
    // Other exception or VM error.
    let mut reason = String::new();
    if !stdout.trim().is_empty() {
        reason.push_str("--- stdout ---\n");
        reason.push_str(stdout.trim_end());
        reason.push('\n');
    }
    if !stderr.trim().is_empty() {
        reason.push_str("--- stderr ---\n");
        reason.push_str(stderr.trim_end());
    }
    if reason.is_empty() {
        reason = format!("z42vm exited with status {}", output.status);
    }
    Outcome::Failed { reason }
}

fn extract_exception_msg(stderr: &str) -> String {
    // Strip the generic "Error: uncaught exception: " prefix to leave just the
    // type + message, e.g. "Std.TestFailure: intentional failure".
    for line in stderr.lines() {
        let trimmed = line.trim_start();
        if let Some(rest) = trimmed.strip_prefix("Error: uncaught exception: ") {
            return rest.trim_end().to_string();
        }
    }
    stderr.lines().next().unwrap_or("(unknown)").to_string()
}

// ── z42vm path resolution ─────────────────────────────────────────────────

fn resolve_z42vm(override_: Option<&PathBuf>) -> Result<PathBuf> {
    if let Some(p) = override_ {
        if p.is_file() { return Ok(p.clone()); }
        bail!("--z42vm path does not exist: {}", p.display());
    }

    // Try sibling of test-runner binary first (most common: artifacts/rust/{debug,release}/).
    if let Ok(self_exe) = std::env::current_exe() {
        if let Some(parent) = self_exe.parent() {
            let cand = parent.join(if cfg!(windows) { "z42vm.exe" } else { "z42vm" });
            if cand.is_file() { return Ok(cand); }
        }
    }

    // Try $PATH.
    let path_var = std::env::var_os("PATH").unwrap_or_default();
    for dir in std::env::split_paths(&path_var) {
        let cand = dir.join(if cfg!(windows) { "z42vm.exe" } else { "z42vm" });
        if cand.is_file() { return Ok(cand); }
    }

    bail!(
        "z42vm binary not found (checked: sibling of test-runner, then $PATH). \
         Run `cargo build --manifest-path src/runtime/Cargo.toml` first or pass --z42vm <path>."
    );
}

// ── Output ────────────────────────────────────────────────────────────────

#[derive(Default)]
struct RunSummary {
    passed: usize,
    failed: usize,
    skipped: usize,
}

fn print_header(report: &TestReport, path: &str) {
    let total = report.tests.len();
    let module_name = std::path::Path::new(path)
        .file_name().and_then(|s| s.to_str()).unwrap_or(path);
    println!("{}", format!("running {total} tests from {module_name}").bold());
    println!();
}

fn print_footer(summary: &RunSummary) {
    println!();
    let line = format!(
        "result: {}.  {} passed; {} failed; {} skipped",
        if summary.failed == 0 {
            "ok".green().bold().to_string()
        } else {
            "FAILED".red().bold().to_string()
        },
        summary.passed,
        summary.failed,
        summary.skipped,
    );
    println!("{}", line);
}
