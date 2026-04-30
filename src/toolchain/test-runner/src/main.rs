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
    /// Resolved [ShouldThrow<E>] expected exception type name (R4.B / A2).
    /// Populated only when SHOULD_THROW flag is set and the type was resolved.
    /// `None` means the test has no ShouldThrow expectation.
    expected_throw: Option<String>,
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
            // R4.B / A2 — only populate when SHOULD_THROW is set AND the type
            // resolved (str_idx == 0 leaves expected_throw_type as None).
            let expected_throw = if entry.flags.contains(TestFlags::SHOULD_THROW) {
                entry.expected_throw_type.clone()
            } else {
                None
            };
            tests.push(DiscoveredTest {
                method_id: entry.method_id,
                method_name: &method.name,
                flags: entry.flags,
                skip_reason,
                expected_throw,
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

    // R4.B / A2 — [ShouldThrow<E>] inverts the success/failure semantics:
    // success means "the expected type was thrown" rather than "no exception".
    if let Some(expected) = &test.expected_throw {
        if output.status.success() {
            return Outcome::Failed {
                reason: format!(
                    "expected to throw `{expected}`, but no exception was thrown"
                ),
            };
        }
        let actual = extract_thrown_type(&stderr);
        match actual.as_deref() {
            Some(a) if type_matches(expected, a) => {
                return Outcome::Passed { duration_ms };
            }
            Some(a) => {
                return Outcome::Failed {
                    reason: format!("expected to throw `{expected}`, got `{a}`"),
                };
            }
            None => {
                return Outcome::Failed {
                    reason: format!(
                        "expected to throw `{expected}`, got non-exception failure: {}",
                        stderr.lines().next().unwrap_or("(empty stderr)").trim_end(),
                    ),
                };
            }
        }
    }

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

/// Extract the (possibly fully-qualified) thrown type name from z42vm's stderr.
///
/// z42vm prints uncaught exceptions in one of two shapes (kept consistent with
/// `interp::format_uncaught_exception`):
///   `Error: uncaught exception: <FQ_TYPE>: <msg>`        — string-message form
///   `Error: uncaught exception: <FQ_TYPE>{field=...}`    — object-dump form
///
/// We pull `<FQ_TYPE>` —— the longest prefix of `[A-Za-z0-9_.]` after the
/// `"Error: uncaught exception: "` literal. Returns `None` when the prefix is
/// absent (e.g. VM crashed before exception machinery).
fn extract_thrown_type(stderr: &str) -> Option<String> {
    const PREFIX: &str = "Error: uncaught exception: ";
    for line in stderr.lines() {
        let trimmed = line.trim_start();
        if let Some(rest) = trimmed.strip_prefix(PREFIX) {
            let end = rest
                .find(|c: char| !(c.is_ascii_alphanumeric() || c == '_' || c == '.'))
                .unwrap_or(rest.len());
            if end == 0 { return None; }
            return Some(rest[..end].to_string());
        }
    }
    None
}

/// Whether `expected` (as written in `[ShouldThrow<E>]`) matches the `actual_fq`
/// fully-qualified type extracted from stderr. Matches when:
///   - strings are byte-equal (`"Std.TestFailure" == "Std.TestFailure"`)
///   - `expected` is the trailing dotted segment of `actual_fq`
///     (`"TestFailure"` matches `"Std.TestFailure"` but not `"MyTestFailure"`)
///
/// Does NOT walk inheritance: `[ShouldThrow<Exception>]` does not match
/// `Std.TestFailure` even though `TestFailure : Exception`. That broader
/// behaviour is left to a future spec (requires runner-side type hierarchy).
fn type_matches(expected: &str, actual_fq: &str) -> bool {
    if expected == actual_fq { return true; }
    // Trailing-segment match: split on `.`, last piece is the short name.
    let actual_short = actual_fq.rsplit('.').next().unwrap_or(actual_fq);
    expected == actual_short
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

// ── Tests ────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    // ── extract_thrown_type ──────────────────────────────────────────────

    #[test]
    fn extract_simple_qualified() {
        let s = "Error: uncaught exception: Std.TestFailure: boom";
        assert_eq!(extract_thrown_type(s), Some("Std.TestFailure".to_string()));
    }

    #[test]
    fn extract_short_name() {
        let s = "Error: uncaught exception: MyError: something";
        assert_eq!(extract_thrown_type(s), Some("MyError".to_string()));
    }

    #[test]
    fn extract_picks_first_matching_line_in_multiline() {
        let s = "some preamble\n  Error: uncaught exception: Std.SkipSignal: skipped\nfooter";
        assert_eq!(extract_thrown_type(s), Some("Std.SkipSignal".to_string()));
    }

    #[test]
    fn extract_no_prefix_returns_none() {
        let s = "panic: VM segfaulted at 0xdeadbeef";
        assert_eq!(extract_thrown_type(s), None);
    }

    #[test]
    fn extract_no_message_separator() {
        // Trailing-only form: no `:`, no `{` — just the type. Pull the whole
        // remaining identifier-like prefix.
        let s = "Error: uncaught exception: Std.TestFailure";
        assert_eq!(extract_thrown_type(s), Some("Std.TestFailure".to_string()));
    }

    #[test]
    fn extract_object_dump_form() {
        // z42vm's actual format for exception objects without a string message:
        // type name followed by `{field=value}` — must stop at the `{`.
        let s = r#"Error: uncaught exception: Std.TestFailure{message="boom"}"#;
        assert_eq!(extract_thrown_type(s), Some("Std.TestFailure".to_string()));
    }

    #[test]
    fn extract_with_indentation() {
        let s = "       Error: uncaught exception: Std.SkipSignal{}";
        assert_eq!(extract_thrown_type(s), Some("Std.SkipSignal".to_string()));
    }

    // ── type_matches ─────────────────────────────────────────────────────

    #[test]
    fn matches_byte_equal() {
        assert!(type_matches("Std.TestFailure", "Std.TestFailure"));
    }

    #[test]
    fn matches_short_against_qualified() {
        assert!(type_matches("TestFailure", "Std.TestFailure"));
        assert!(type_matches("SkipSignal", "Std.SkipSignal"));
    }

    #[test]
    fn no_false_positive_on_suffix_collision() {
        // Crucial: substring containment must NOT trigger a match.
        assert!(!type_matches("TestFailure", "Std.MyTestFailure"));
        assert!(!type_matches("Failure", "Std.TestFailure"));
    }

    #[test]
    fn no_match_on_different_short_names() {
        assert!(!type_matches("TestFailure", "Std.SkipSignal"));
        assert!(!type_matches("Foo", "Bar"));
    }

    #[test]
    fn empty_strings_no_match() {
        // Empty `expected` could falsely match anything via short-name suffix
        // logic if not guarded — make sure it doesn't. Both sides empty are
        // technically equal but this case shouldn't arise (validator E0913
        // prevents bare `[ShouldThrow]` from reaching the runner).
        assert!(!type_matches("", "Std.TestFailure"));
        assert!(!type_matches("Foo", ""));
    }

    #[test]
    fn matches_short_against_short() {
        // No namespace on the actual side (rare but possible).
        assert!(type_matches("TestFailure", "TestFailure"));
    }
}
