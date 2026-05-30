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
mod exec;       // legacy subprocess path (kept for `--legacy-subprocess` fallback)
mod format;
mod parallel;   // add-test-runner-parallel: --jobs N >1 worker pool
mod result;
mod runner;     // in-process Setup/Test/Teardown chain (R3b default)
mod skip_eval;  // add-test-skip-platform-feature-eval: conditional [Skip] evaluator

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

    /// Number of parallel worker threads. Each worker forks `z42vm` per
    /// test. Default: 1 (serial, no thread pool — preserves Setup/Teardown).
    /// Pass `--jobs 0` to auto-detect from `available_parallelism()`.
    ///
    /// add-test-runner-parallel (2026-05-27): N>1 implies `--legacy-
    /// subprocess` since `VmContext` is `!Send`. Setup/Teardown hooks
    /// are skipped in subprocess mode — use `--jobs 1` (default) if you
    /// need them.
    #[arg(long, value_name = "N")]
    jobs: Option<usize>,

    /// Override host platform detection for `[Skip(platform: ...)]`
    /// evaluation (default: `std::env::consts::OS`). Useful when verifying
    /// platform-gated tests across hosts without booting a second OS.
    /// `Z42_TEST_PLATFORM` env var is consulted when this flag is absent;
    /// the flag wins if both are set.
    /// add-test-skip-platform-feature-eval (2026-05-30).
    #[arg(long, value_name = "NAME")]
    platform: Option<String>,

    /// Print the discovered test names (one per line) to stdout and exit.
    /// Filter (--filter) still applies. Useful for CI sharding —
    /// `runner suite.zbc --list | sort -u | split -n l/$N` partitions the
    /// test set across N jobs. Wins over `--dry-run` when both are set.
    /// add-runner-list-and-dry-run-flags (2026-05-31).
    #[arg(long)]
    list: bool,

    /// Walk discovery + filter + skip evaluation as usual, then synthesize
    /// `Passed { duration_ms: 0 }` for every test that survives — no actual
    /// body invocation. `[Skip(...)]` entries still report as Skipped
    /// (skip eval runs). Useful for verifying filter / platform / feature
    /// gating logic without paying execution cost. Cannot be combined with
    /// `--list` (which short-circuits earlier).
    /// add-runner-list-and-dry-run-flags (2026-05-31).
    #[arg(long)]
    dry_run: bool,
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

    // add-test-skip-platform-feature-eval (2026-05-30): SkipEnv carries the
    // current platform + available feature set used to evaluate
    // `[Skip(platform: …)]` / `[Skip(feature: …)]` conditionally. CLI flag
    // wins; otherwise env var; otherwise std::env::consts::OS.
    let mut skip_env = skip_eval::SkipEnv::detect();
    let override_platform = cli
        .platform
        .clone()
        .or_else(|| std::env::var("Z42_TEST_PLATFORM").ok());
    if let Some(p) = override_platform {
        skip_env = skip_env.with_platform(p);
    }

    // add-test-runner-parallel (2026-05-27): --jobs N (N>1) forces
    // subprocess mode since VmContext is !Send. Resolve N first so the
    // routing decision is explicit.
    let jobs = resolve_jobs(cli.jobs);
    let use_subprocess = cli.legacy_subprocess || jobs > 1;
    if jobs > 1 && !cli.legacy_subprocess {
        eprintln!(
            "{}: --jobs {} forces subprocess execution; [Setup]/[Teardown] will not run",
            "note".yellow(), jobs);
    }

    if use_subprocess {
        // Legacy path: subprocess fork per test (no Setup/Teardown).
        let artifact = z42::metadata::load_artifact(zbc_path)
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
        // add-runner-list-and-dry-run-flags (2026-05-31): short-circuit
        // before subprocess fork costs.
        if cli.list {
            for t in &report.tests { println!("{}", t.method_name); }
            return Ok(0);
        }
        if cli.dry_run {
            let results: Vec<TestResult> = report.tests.iter()
                .map(|t| TestResult::from_outcome(
                    t.method_name.to_string(),
                    dry_run_outcome(t, &skip_env),
                    t.is_benchmark))
                .collect();
            emit(&format, &module_name, &results)?;
            return Ok(0);
        }
        let z42vm = exec::resolve_z42vm(cli.z42vm.as_ref())
            .context("locating z42vm binary (use --z42vm to override)")?;
        let results: Vec<TestResult> = if jobs > 1 {
            // Parallel subprocess pool.
            parallel::run_tests(&z42vm, zbc_path, &report.tests, jobs, &skip_env)
        } else {
            // Serial legacy path.
            report.tests.iter()
                .map(|t| TestResult::from_outcome(
                    t.method_name.to_string(),
                    exec::run_one(&z42vm, zbc_path, t, &skip_env),
                    t.is_benchmark))
                .collect()
        };
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
            // add-benchmark-runner-dispatch (2026-05-31): accept both Test
            // and Benchmark; Benchmark is dispatched identically (zero-arg)
            // but labelled separately in pretty output.
            if !matches!(
                entry.kind,
                z42::metadata::TestEntryKind::Test | z42::metadata::TestEntryKind::Benchmark
            ) {
                return None;
            }
            if entry.flags.contains(z42::metadata::TestFlags::IGNORED) { return None; }
            let name = loaded.user_func_names.get(entry.method_id as usize)?.clone();
            if let Some(needle) = &cli.filter {
                if !name.contains(needle.as_str()) { return None; }
            }
            // add-test-skip-platform-feature-eval (2026-05-30): preserve the
            // three skip segments as separate fields so the in-process loop
            // can hand them to skip_eval::decide_skip per test.
            let (skip_reason, skip_platform, skip_feature) =
                if entry.flags.contains(z42::metadata::TestFlags::SKIPPED) {
                    (
                        entry.skip_reason.clone(),
                        entry.skip_platform.clone(),
                        entry.skip_feature.clone(),
                    )
                } else {
                    (None, None, None)
                };
            let expected_throw = if entry.flags.contains(z42::metadata::TestFlags::SHOULD_THROW) {
                entry.expected_throw_type.clone()
            } else { None };
            Some(DiscoveredTestOwned {
                method_id: entry.method_id,
                method_name: name,
                flags: entry.flags,
                is_benchmark: entry.kind == z42::metadata::TestEntryKind::Benchmark,
                skip_reason,
                skip_platform,
                skip_feature,
                expected_throw,
                timeout_ms: if entry.timeout_ms == 0 { None } else { Some(entry.timeout_ms) },
            })
        })
        .collect();

    if report_tests.is_empty() {
        if matches!(format, Format::Pretty) {
            println!("{}", "no tests found (TIDX section empty, absent, or filtered out)".yellow());
        }
        return Ok(3);
    }

    // add-runner-list-and-dry-run-flags (2026-05-31): short-circuit before
    // the per-test runner::run_one loop costs.
    if cli.list {
        for t in &report_tests { println!("{}", t.method_name); }
        return Ok(0);
    }
    if cli.dry_run {
        let results: Vec<TestResult> = report_tests.iter()
            .map(|t| {
                let dt = discover::DiscoveredTest {
                    method_id: t.method_id,
                    method_name: &t.method_name,
                    flags: t.flags,
                    is_benchmark: t.is_benchmark,
                    skip_reason: t.skip_reason.clone(),
                    skip_platform: t.skip_platform.clone(),
                    skip_feature: t.skip_feature.clone(),
                    expected_throw: t.expected_throw.clone(),
                    timeout_ms: t.timeout_ms,
                };
                TestResult::from_outcome(
                    t.method_name.clone(),
                    dry_run_outcome(&dt, &skip_env),
                    t.is_benchmark)
            })
            .collect();
        emit(&format, &module_name, &results)?;
        return Ok(0);
    }

    let mut results: Vec<TestResult> = Vec::with_capacity(report_tests.len());
    for test in &report_tests {
        // Borrow as DiscoveredTest<'_> for runner API compat.
        let dt = discover::DiscoveredTest {
            method_id: test.method_id,
            method_name: &test.method_name,
            flags: test.flags,
            is_benchmark: test.is_benchmark,
            skip_reason: test.skip_reason.clone(),
            skip_platform: test.skip_platform.clone(),
            skip_feature: test.skip_feature.clone(),
            expected_throw: test.expected_throw.clone(),
            timeout_ms: test.timeout_ms,
        };
        let outcome = runner::run_one(&mut loaded, &dt, &skip_env);
        results.push(TestResult::from_outcome(
            test.method_name.clone(),
            outcome,
            test.is_benchmark,
        ));
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
    flags: z42::metadata::TestFlags,
    /// add-benchmark-runner-dispatch (2026-05-31): kind=Benchmark routed
    /// through the same execution path as Test, but tagged for display.
    is_benchmark: bool,
    skip_reason: Option<String>,
    /// add-test-skip-platform-feature-eval (2026-05-30): split out so the
    /// runner can evaluate `[Skip(platform: …)]` against the current host
    /// instead of unconditionally skipping every flagged test.
    skip_platform: Option<String>,
    skip_feature: Option<String>,
    expected_throw: Option<String>,
    /// add-test-timeout-attribute (2026-05-30): per-test override from
    /// `[Timeout(milliseconds: N)]`. None = use runner default.
    timeout_ms: Option<u32>,
}

fn emit(format: &Format, module_name: &str, results: &[TestResult]) -> Result<()> {
    match format {
        Format::Pretty => format::pretty::print(module_name, results),
        Format::Tap    => format::tap::print(module_name, results),
        Format::Json   => format::json::print(module_name, results)?,
    }
    Ok(())
}

/// add-runner-list-and-dry-run-flags (2026-05-31): synthesize an Outcome
/// for `--dry-run` without invoking the test body.
///
/// Skip evaluation still runs — a `[Skip(platform: "ios")]` test on iOS
/// reports as Skipped (with the proper reason string), matching real-run
/// behavior. Tests that survive skip eval report as Passed with zero
/// duration. Useful for verifying filter / platform / feature gating
/// in CI before paying execution cost.
fn dry_run_outcome(test: &discover::DiscoveredTest<'_>, env: &skip_eval::SkipEnv) -> result::Outcome {
    if let Some(reason) = skip_eval::decide_skip(test, env) {
        return result::Outcome::Skipped { reason };
    }
    result::Outcome::Passed { duration_ms: 0 }
}

/// add-test-runner-parallel (2026-05-27): resolve --jobs N.
/// - `None` → 1 (preserve current default; no behavior change for callers
///   that don't opt in).
/// - `Some(0)` → auto-detect via `available_parallelism()` (rayon-style
///   "use all CPUs" sentinel).
/// - `Some(n ≥ 1024)` → also auto-detect (defensive cap; callers passing
///   `--jobs 9999` clearly want "as many as possible", not literal 9999
///   subprocess forks).
/// - `Some(n)` (1 ≤ n < 1024) → verbatim.
fn resolve_jobs(cli_jobs: Option<usize>) -> usize {
    match cli_jobs {
        None    => 1,
        Some(0) | Some(1024..) => std::thread::available_parallelism()
            .map(|p| p.get())
            .unwrap_or(1),
        Some(n) => n,
    }
}
