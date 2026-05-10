//! z42-test-runner — drives [Test] / [Benchmark] / [Skip] / [Ignore] /
//! [ShouldThrow<E>] decorated z42 functions discovered from the zbc TIDX
//! section (R1 add-test-metadata-section).
//!
//! 当前 R3a 实现状态:
//! - **Discovery (in-process)**：读 `LoadedArtifact.test_index`，O(1) 启动
//! - **Execution (subprocess)**：每个 [Test] fork z42vm 子进程跑 `--entry`，
//!   分类 stderr/exit code 得到 Outcome。Setup/Teardown 暂未生效。
//! - **Formatters**：pretty / TAP 13 / JSON 三种（CLI `--format` 切换；
//!   默认按 stdout TTY 自动选）
//!
//! 推迟到 R3b（spec `rewrite-z42-test-runner-compile-time` 实施中）:
//! - **Execution (in-process)**：替换 subprocess 为 `interp::run`
//! - **[Setup] / [Teardown] hook 真生效**（当前完全无效）
//! - **[Benchmark] runner 调度**（R2 已有 Bencher 类，runner 缺触发）
//! - **zpkg-as-input**
//!
//! 用法：
//!   z42-test-runner <file.zbc> [--format <pretty|tap|json>] [--filter <SUBSTR>]
//!
//! 模块组织（rewrite-z42-test-runner-compile-time S1, 2026-05-10）:
//! - `discover`: TIDX → DiscoveredTest / TestReport
//! - `result`:   TestStatus / TestResult / Summary / Outcome
//! - `exec`:     run_one (subprocess) + 异常分类 + z42vm 路径解析
//! - `format`:   pretty / tap / json formatters + Format enum

mod discover;
mod exec;
mod format;
mod result;

use anyhow::{Context, Result};
use clap::Parser;
use colored::*;
use std::path::PathBuf;

use z42_vm::metadata::load_artifact;

use crate::discover::TestReport;
use crate::format::{resolve_format, Format};
use crate::result::{TestResult, TestStatus};

#[derive(Parser)]
#[command(name = "z42-test-runner", about = "z42 test runner (R3a)", version)]
struct Cli {
    /// Path to a .zbc file containing test methods (decorated with z42.test attributes).
    file: PathBuf,

    /// Output format. Default: `pretty` when stdout is a TTY, otherwise `tap`.
    #[arg(long, value_enum)]
    format: Option<Format>,

    /// Run only tests whose method name contains this substring (case-sensitive).
    #[arg(long, value_name = "SUBSTR")]
    filter: Option<String>,

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
    let format = resolve_format(cli.format);

    let mut report = TestReport::from_artifact(&artifact);
    // R3a — substring filter applied post-discovery; preserves zero-test
    // semantics so an over-narrow filter exits 3 (not 0) just like an empty
    // TIDX section.
    if let Some(needle) = &cli.filter {
        report.tests.retain(|t| t.method_name.contains(needle.as_str()));
    }
    if report.tests.is_empty() {
        if matches!(format, Format::Pretty) {
            println!("{}", "no tests found (TIDX section empty, absent, or filtered out)".yellow());
        }
        return Ok(3);
    }

    let z42vm = exec::resolve_z42vm(cli.z42vm.as_ref())
        .context("locating z42vm binary (use --z42vm to override)")?;

    let module_name = std::path::Path::new(zbc_path)
        .file_name().and_then(|s| s.to_str()).unwrap_or(zbc_path).to_string();

    let mut results: Vec<TestResult> = Vec::with_capacity(report.tests.len());
    for test in &report.tests {
        let outcome = exec::run_one(&z42vm, zbc_path, test);
        results.push(TestResult::from_outcome(test.method_name.to_string(), outcome));
    }

    let exit_code = if results.iter().any(|r| r.status == TestStatus::Failed) { 1 } else { 0 };

    match format {
        Format::Pretty => format::pretty::print(&module_name, &results),
        Format::Tap    => format::tap::print(&module_name, &results),
        Format::Json   => format::json::print(&module_name, &results)?,
    }
    Ok(exit_code)
}
