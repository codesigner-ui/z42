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

mod bootstrap;
mod discover;
mod exec;     // legacy subprocess path (kept for `--legacy-subprocess` fallback)
mod format;
mod result;
mod runner;   // in-process Setup/Test/Teardown chain (R3b default)

use anyhow::{Context, Result};
use clap::Parser;
use colored::*;
use std::path::PathBuf;

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

    /// Override z42vm binary path (legacy subprocess mode only).
    #[arg(long)]
    z42vm: Option<PathBuf>,

    /// Force-disable color output (default: auto-detect TTY).
    #[arg(long)]
    no_color: bool,

    /// Use legacy subprocess execution (fork z42vm per test) instead of the
    /// R3b in-process runner. Useful as a fallback if in-process exposes
    /// runtime regressions; will be removed once R3b is stable.
    #[arg(long)]
    legacy_subprocess: bool,
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
    let format = resolve_format(cli.format);
    let module_name = std::path::Path::new(zbc_path)
        .file_name().and_then(|s| s.to_str()).unwrap_or(zbc_path).to_string();

    if cli.legacy_subprocess {
        // Legacy path: subprocess fork per test (no Setup/Teardown).
        let artifact = z42_vm::metadata::load_artifact(zbc_path)
            .with_context(|| format!("loading artifact `{zbc_path}`"))?;
        let mut report = TestReport::from_artifact(&artifact);
        if let Some(needle) = &cli.filter {
            report.tests.retain(|t| t.method_name.contains(needle.as_str()));
        }
        if report.tests.is_empty() {
            if matches!(format, Format::Pretty) {
                println!("{}", "no tests found".yellow());
            }
            return Ok(3);
        }
        let z42vm = exec::resolve_z42vm(cli.z42vm.as_ref())
            .context("locating z42vm binary (use --z42vm to override)")?;
        let mut results: Vec<TestResult> = Vec::with_capacity(report.tests.len());
        for test in &report.tests {
            let outcome = exec::run_one(&z42vm, zbc_path, test);
            results.push(TestResult::from_outcome(test.method_name.to_string(), outcome));
        }
        emit(&format, &module_name, &results)?;
        let exit_code = if results.iter().any(|r| r.status == TestStatus::Failed) { 1 } else { 0 };
        return Ok(exit_code);
    }

    // R3b in-process path (default).
    let mut loaded = bootstrap::bootstrap(zbc_path)
        .with_context(|| format!("bootstrapping in-process VM for `{zbc_path}`"))?;

    // Build owned-name discovery list to break the borrow tie with `loaded`
    // during the per-test run loop (DiscoveredTest borrows method_name from
    // user_func_names, but run_one needs &mut loaded).
    let report_tests: Vec<DiscoveredTestOwned> = loaded.test_index.iter()
        .filter_map(|entry| {
            if entry.kind != z42_vm::metadata::TestEntryKind::Test { return None; }
            if entry.flags.contains(z42_vm::metadata::TestFlags::IGNORED) { return None; }
            let name = loaded.user_func_names.get(entry.method_id as usize)?.clone();
            if let Some(needle) = &cli.filter {
                if !name.contains(needle.as_str()) { return None; }
            }
            let skip_reason = if entry.flags.contains(z42_vm::metadata::TestFlags::SKIPPED) {
                Some(discover::format_skip_reason(entry))
            } else { None };
            let expected_throw = if entry.flags.contains(z42_vm::metadata::TestFlags::SHOULD_THROW) {
                entry.expected_throw_type.clone()
            } else { None };
            Some(DiscoveredTestOwned {
                method_id: entry.method_id,
                method_name: name,
                flags: entry.flags,
                skip_reason,
                expected_throw,
            })
        })
        .collect();

    if report_tests.is_empty() {
        if matches!(format, Format::Pretty) {
            println!("{}", "no tests found (TIDX section empty, absent, or filtered out)".yellow());
        }
        return Ok(3);
    }

    let mut results: Vec<TestResult> = Vec::with_capacity(report_tests.len());
    for test in &report_tests {
        // Borrow as DiscoveredTest<'_> for runner API compat.
        let dt = discover::DiscoveredTest {
            method_id: test.method_id,
            method_name: &test.method_name,
            flags: test.flags,
            skip_reason: test.skip_reason.clone(),
            expected_throw: test.expected_throw.clone(),
        };
        let outcome = runner::run_one(&mut loaded, &dt);
        results.push(TestResult::from_outcome(test.method_name.clone(), outcome));
    }
    emit(&format, &module_name, &results)?;
    let exit_code = if results.iter().any(|r| r.status == TestStatus::Failed) { 1 } else { 0 };
    Ok(exit_code)
}

/// Owned-name variant of [`discover::DiscoveredTest`] — used to break the
/// borrow tie between TestReport (borrows from `LoadedRunner.user_func_names`)
/// and the per-test mutable VmContext access in the run loop.
struct DiscoveredTestOwned {
    #[allow(dead_code)] method_id: u32,
    method_name: String,
    flags: z42_vm::metadata::TestFlags,
    skip_reason: Option<String>,
    expected_throw: Option<String>,
}

fn emit(format: &Format, module_name: &str, results: &[TestResult]) -> Result<()> {
    match format {
        Format::Pretty => format::pretty::print(module_name, results),
        Format::Tap    => format::tap::print(module_name, results),
        Format::Json   => format::json::print(module_name, results)?,
    }
    Ok(())
}
