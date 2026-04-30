//! z42-test-runner — minimal R3 implementation (spec
//! `rewrite-z42-test-runner-compile-time`).
//!
//! 当前 R3 minimal scope（受 closure 缺失限制）:
//! - 加载单个 .zbc 文件
//! - 从 TIDX section 读 TestEntry 列表
//! - 顺序跑每个 [Test]：fresh VmContext → Setup → 测试体 → Teardown
//! - 异常分类：TestFailure → 失败、SkipSignal → 跳过、其他 Exception → 失败
//! - Pretty 输出（带颜色，TTY 检测）
//! - 退出码：0 全过 / 1 有失败 / 2 工具错误 / 3 无测试
//!
//! 推迟（依赖 closure / 多 .zbc / 等待后续 spec）:
//! - --filter / --tag / --bench / TAP / JSON formatter (R3 完整版)
//! - Bencher 模式 (依赖 closure → R2.C)
//! - TestCase 参数化 (依赖 typed args → R4)
//! - 并行执行 (R6 / v0.2)
//! - 多 .zbc 同时跑 (R3 完整版)
//!
//! 使用：
//!   z42-test-runner <file.zbc>

use anyhow::{Context, Result};
use clap::Parser;
use colored::*;
use std::path::PathBuf;

use z42_vm::metadata::{
    load_artifact, ExecMode, LoadedArtifact, TestEntry, TestEntryKind, TestFlags,
};
use z42_vm::vm::Vm;
use z42_vm::vm_context::VmContext;

#[derive(Parser)]
#[command(name = "z42-test-runner", about = "z42 test runner (R3 minimal)", version)]
struct Cli {
    /// Path to a .zbc file containing test methods (decorated with z42.test attributes).
    file: PathBuf,

    /// Force-disable color output (default: auto-detect TTY).
    #[arg(long)]
    no_color: bool,

    /// Show stdout for passing tests too (default: only on failure — capture not yet implemented).
    #[arg(long)]
    show_output: bool,
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
    let path = cli.file.to_str().context("file path is not valid UTF-8")?;
    let artifact = load_artifact(path)
        .with_context(|| format!("loading artifact `{path}`"))?;

    let report = TestReport::from_artifact(&artifact);
    if report.tests.is_empty() {
        println!("{}", "no tests found (TIDX section empty or absent)".yellow());
        return Ok(3);
    }

    print_header(&report, path);
    let mut summary = RunSummary::default();

    let vm = Vm::new(reload_module(path)?, ExecMode::Interp);

    for test in &report.tests {
        let outcome = run_one(&vm, test, &report);
        match outcome {
            Outcome::Passed { duration_ms } => {
                summary.passed += 1;
                println!(
                    "  {} {}  ({}ms)",
                    "✓".green().bold(),
                    test.method_name,
                    duration_ms
                );
            }
            Outcome::Skipped { reason } => {
                summary.skipped += 1;
                println!(
                    "  {} {}  ({})",
                    "⊘".yellow().bold(),
                    test.method_name,
                    reason
                );
            }
            Outcome::Failed { reason } => {
                summary.failed += 1;
                println!(
                    "  {} {}",
                    "✗".red().bold(),
                    test.method_name
                );
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

// Reserved fields (`method_id`, `flags`) kept for the upcoming R3 full impl
// (filtering by name, --include-ignored, etc.). Allow dead_code in v0.1.
#[allow(dead_code)]
struct DiscoveredTest<'a> {
    method_id: u32,
    method_name: &'a str,
    flags: TestFlags,
    skip_reason: Option<String>,
}

struct TestReport<'a> {
    tests: Vec<DiscoveredTest<'a>>,
    setups: Vec<u32>,
    teardowns: Vec<u32>,
}

impl<'a> TestReport<'a> {
    fn from_artifact(artifact: &'a LoadedArtifact) -> Self {
        let mut tests = Vec::new();
        let mut setups = Vec::new();
        let mut teardowns = Vec::new();

        for entry in &artifact.test_index {
            match entry.kind {
                TestEntryKind::Test => {
                    if entry.flags.contains(TestFlags::IGNORED) {
                        // [Ignore] — skip silently per design.
                        continue;
                    }
                    let method = &artifact.module.functions[entry.method_id as usize];
                    let skip_reason = if entry.flags.contains(TestFlags::SKIPPED) {
                        // Strings resolved by loader. Combine platform /
                        // feature / reason into a single display blob.
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
                TestEntryKind::Setup    => setups.push(entry.method_id),
                TestEntryKind::Teardown => teardowns.push(entry.method_id),
                _ => {} // Benchmark / Doctest — not handled in R3 minimal
            }
        }

        Self { tests, setups, teardowns }
    }
}

fn format_skip_reason(entry: &TestEntry) -> String {
    // Build a "platform=ios; feature=jit; reason=..." style display string
    // from the resolved (Option<String>) fields. Empty if all None.
    let mut parts: Vec<String> = Vec::new();
    if let Some(p) = &entry.skip_platform { parts.push(format!("platform={p}")); }
    if let Some(f) = &entry.skip_feature  { parts.push(format!("feature={f}"));  }
    if let Some(r) = &entry.skip_reason   { parts.push(r.clone()); }
    if parts.is_empty() { "skipped".into() } else { parts.join("; ") }
}

// ── Execution ─────────────────────────────────────────────────────────────

enum Outcome {
    Passed { duration_ms: u64 },
    Failed { reason: String },
    Skipped { reason: String },
}

fn run_one(vm: &Vm, test: &DiscoveredTest, report: &TestReport) -> Outcome {
    if let Some(reason) = &test.skip_reason {
        return Outcome::Skipped { reason: reason.clone() };
    }

    let start = std::time::Instant::now();
    let mut ctx = VmContext::new();

    // Setup methods (in declaration order)
    for &setup_id in &report.setups {
        let setup_method = &vm.module.functions[setup_id as usize];
        if let Err(e) = vm.run(&mut ctx, Some(&setup_method.name)) {
            return Outcome::Failed { reason: format!("setup `{}` failed: {e:#}", setup_method.name) };
        }
    }

    // Test body
    let result = vm.run(&mut ctx, Some(test.method_name));

    // Teardown methods (always — even on failure)
    for &teardown_id in &report.teardowns {
        let teardown_method = &vm.module.functions[teardown_id as usize];
        if let Err(e) = vm.run(&mut ctx, Some(&teardown_method.name)) {
            // Teardown failure: log but don't override main outcome.
            eprintln!("  {} teardown `{}` failed: {e:#}", "!".yellow(), teardown_method.name);
        }
    }

    let duration_ms = start.elapsed().as_millis() as u64;

    match result {
        Ok(()) => Outcome::Passed { duration_ms },
        Err(e) => classify_error(e),
    }
}

fn classify_error(e: anyhow::Error) -> Outcome {
    let msg = format!("{e:#}");
    // Heuristic classification by Exception type name in error message.
    // R4 will add typed expected_throw_type lookup for [ShouldThrow<E>].
    if msg.contains("SkipSignal") {
        // Extract reason after the type marker if present.
        let reason = msg.lines()
            .find(|l| l.contains("SkipSignal:"))
            .and_then(|l| l.split("SkipSignal:").nth(1))
            .map(|s| s.trim().to_string())
            .unwrap_or_else(|| msg.clone());
        Outcome::Skipped { reason }
    } else {
        Outcome::Failed { reason: msg }
    }
}

// ── Module reloading helper ───────────────────────────────────────────────

/// Reload the Module fresh because Vm::new takes ownership and Module is not
/// Clone. Cheap enough for current test sizes; future R3 will share via Arc.
fn reload_module(path: &str) -> Result<z42_vm::metadata::bytecode::Module> {
    let artifact = load_artifact(path)
        .with_context(|| format!("reloading artifact `{path}`"))?;
    Ok(artifact.module)
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
        if summary.failed == 0 { "ok".green().bold().to_string() } else { "FAILED".red().bold().to_string() },
        summary.passed,
        summary.failed,
        summary.skipped,
    );
    println!("{}", line);
}
