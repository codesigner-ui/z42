//! Test execution — currently subprocess-based; in-process replacement
//! lands in S3 of `rewrite-z42-test-runner-compile-time`.
//!
//! Phase: rewrite-z42-test-runner-compile-time S1 (2026-05-10) — extracted
//! from monolithic `main.rs`. Behavior unchanged from R3a.

use anyhow::{bail, Result};
use std::io::Read;
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use crate::discover::DiscoveredTest;
use crate::result::Outcome;
use crate::skip_eval::{decide_skip, SkipEnv};

/// Per-test wallclock cap used when a method does NOT carry an explicit
/// `[Timeout(milliseconds: N)]`. Generous vs. observed legitimate test
/// durations:
///   - Most [Test] methods complete in milliseconds
///   - Slowest JIT/compression ones ~30 s on cold caches
///   - **ECDSA secp256k1 sign / verify round-trips on CI runners (3-4
///     vCPU) take 60-180 s** because the z42-stdlib BigInt path is pure
///     z42 + does naive modular exponentiation (no Montgomery / no
///     windowed mul yet). 120 s caught these as false-positive
///     "timeouts" on ubuntu/macos/arm CI. 300 s leaves headroom for slow
///     runners without weakening the hang detector (genuine hangs go
///     forever, so any finite cap above legitimate max suffices).
const DEFAULT_TIMEOUT_SECS: u64 = 300;

/// Hard ceiling for any per-test override. A typo like `[Timeout(milliseconds:
/// 60_000_000)]` (intended `60_000`) would otherwise disable the hang detector
/// entirely under the GitHub Actions 6 h job limit. We clamp to 2× default and
/// print a one-line warning so the user sees the override didn't fully land.
/// add-test-timeout-attribute (2026-05-30).
const TIMEOUT_HARD_CEILING_SECS: u64 = DEFAULT_TIMEOUT_SECS * 2;

/// Resolve a per-test wallclock budget from the optional override carried in
/// `DiscoveredTest.timeout_ms`. Returned tuple is `(budget, origin, clamped)`:
/// - `origin` is a human label for diagnostic messages.
/// - `clamped == true` means the requested override exceeded
///   `TIMEOUT_HARD_CEILING_SECS` and was reduced to the ceiling.
///
/// add-test-timeout-attribute (2026-05-30) — extracted from `run_one` for
/// unit testability.
pub(crate) fn compute_budget(timeout_ms: Option<u32>) -> (Duration, &'static str, bool) {
    let ceiling = Duration::from_secs(TIMEOUT_HARD_CEILING_SECS);
    let requested = timeout_ms
        .map(|ms| Duration::from_millis(ms as u64))
        .unwrap_or_else(|| Duration::from_secs(DEFAULT_TIMEOUT_SECS));
    let origin = if timeout_ms.is_some() { "per-method [Timeout]" } else { "runner default" };
    if requested > ceiling {
        (ceiling, origin, true)
    } else {
        (requested, origin, false)
    }
}

pub fn run_one(
    z42vm: &PathBuf,
    zbc_path: &str,
    test: &DiscoveredTest,
    skip_env: &SkipEnv,
) -> Outcome {
    // add-test-skip-platform-feature-eval (2026-05-30): conditional skip
    // (see runner.rs for the in-process twin of this branch).
    if let Some(reason) = decide_skip(test, skip_env) {
        return Outcome::Skipped { reason };
    }

    let start = Instant::now();
    let mut child = match Command::new(z42vm)
        .arg(zbc_path)
        .arg(test.method_name)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
    {
        Ok(c) => c,
        Err(e) => return Outcome::Failed {
            reason: format!("failed to spawn z42vm: {e}")
        },
    };
    let child_pid = child.id();

    // Drain stdout/stderr in background threads so the child never
    // blocks on a full OS pipe buffer (default 64 KB on Linux). Without
    // this, a test that prints a lot of diagnostic output before
    // exiting would deadlock against our try_wait loop instead of
    // exiting normally.
    let mut stdout_pipe = child.stdout.take().expect("stdout was piped");
    let mut stderr_pipe = child.stderr.take().expect("stderr was piped");
    let out_handle = std::thread::spawn(move || {
        let mut buf = Vec::new();
        let _ = stdout_pipe.read_to_end(&mut buf);
        buf
    });
    let err_handle = std::thread::spawn(move || {
        let mut buf = Vec::new();
        let _ = stderr_pipe.read_to_end(&mut buf);
        buf
    });

    // Poll-with-deadline loop. Sleep granularity (50 ms) balances
    // responsiveness for fast tests against syscall overhead — most
    // tests exit in 1-2 sleeps; the timeout path only fires for genuine
    // hangs at the upper bound.
    //
    // add-test-timeout-attribute (2026-05-30): budget resolution is
    //   per-test override (`[Timeout(milliseconds: N)]`)  if present,
    //   else `DEFAULT_TIMEOUT_SECS`.
    // A `TIMEOUT_HARD_CEILING_SECS = 2 × default` clamp protects against
    // typos that would otherwise disable the hang detector.
    let (budget, _origin, clamped) = compute_budget(test.timeout_ms);
    if clamped {
        eprintln!(
            "note: [Timeout] for `{}` requested {} ms exceeds hard ceiling {} ms — clamped",
            test.method_name,
            test.timeout_ms.unwrap_or(0),
            Duration::from_secs(TIMEOUT_HARD_CEILING_SECS).as_millis(),
        );
    }
    let deadline = Instant::now() + budget;
    let mut timed_out = false;
    let mut stack_trace: Option<String> = None;
    let exit_status = loop {
        match child.try_wait() {
            Ok(Some(s)) => break Some(s),
            Ok(None) => {
                if Instant::now() >= deadline {
                    // diag-timeout-stack-trace (2026-05-30): before SIGKILL,
                    // try to snapshot the hung process's thread stacks via
                    // the platform's native sampler (`sample` on macOS,
                    // `gdb --batch` on Linux). Windows has no widely-
                    // available command-line equivalent; we fall back to
                    // "stack trace not captured" there. Best-effort — if
                    // the tool is missing or fails (CI runners without
                    // sudo to attach to processes, etc.), we just note it
                    // and proceed with the kill so the timeout still
                    // surfaces as TIMEOUT instead of a hang.
                    stack_trace = capture_stack_trace(child_pid);
                    let _ = child.kill();
                    timed_out = true;
                    break child.wait().ok();
                }
                std::thread::sleep(Duration::from_millis(50));
            }
            Err(e) => return Outcome::Failed {
                reason: format!("error waiting for z42vm: {e}"),
            },
        }
    };

    let duration_ms = start.elapsed().as_millis() as u64;
    let stdout_buf = out_handle.join().unwrap_or_default();
    let stderr_buf = err_handle.join().unwrap_or_default();
    let stderr = String::from_utf8_lossy(&stderr_buf);
    let stdout = String::from_utf8_lossy(&stdout_buf);

    if timed_out {
        let budget_secs = budget.as_secs_f64();
        let (_, budget_origin, _) = compute_budget(test.timeout_ms);
        let mut reason = format!(
            "timed out after {:.2}s ({}) — z42vm killed (likely hung)\n\
             test:   {}\n\
             zbc:    {}\n\
             pid:    {}\n\
             wall:   {} ms",
            budget_secs,
            budget_origin,
            test.method_name,
            zbc_path,
            child_pid,
            duration_ms,
        );
        if let Some(ref bt) = stack_trace {
            reason.push_str("\n--- thread backtrace (pre-kill) ---\n");
            reason.push_str(bt.trim_end());
        } else {
            reason.push_str(
                "\n--- thread backtrace ---\n(not captured — platform sampler unavailable)",
            );
        }
        if !stdout.trim().is_empty() {
            reason.push_str("\n--- stdout (partial) ---\n");
            reason.push_str(stdout.trim_end());
        }
        if !stderr.trim().is_empty() {
            reason.push_str("\n--- stderr (partial) ---\n");
            reason.push_str(stderr.trim_end());
        }
        return Outcome::Failed { reason };
    }

    let status = exit_status.expect("non-timeout path always has a status");

    // R4.B / A2 — [ShouldThrow<E>] inverts the success/failure semantics:
    // success means "the expected type was thrown" rather than "no exception".
    //
    // A3: `expected_throw` may be a `;`-delimited list "Leaf;Mid;Base"
    // produced by the C# IrGen for inheritance-aware matching (the head is
    // the user-written type, followed by its ancestor short names). The runner
    // accepts a match against ANY entry. Single-name (no `;`) preserves A2 behavior.
    if let Some(expected) = &test.expected_throw {
        let candidates: Vec<&str> = expected.split(';').filter(|s| !s.is_empty()).collect();
        let display = candidates.first().copied().unwrap_or(expected);
        if status.success() {
            return Outcome::Failed {
                reason: format!(
                    "expected to throw `{display}`, but no exception was thrown"
                ),
            };
        }
        let actual = extract_thrown_type(&stderr);
        match actual.as_deref() {
            Some(a) if candidates.iter().any(|c| type_matches(c, a)) => {
                return Outcome::Passed { duration_ms };
            }
            Some(a) => {
                return Outcome::Failed {
                    reason: format!("expected to throw `{display}`, got `{a}`"),
                };
            }
            None => {
                return Outcome::Failed {
                    reason: format!(
                        "expected to throw `{display}`, got non-exception failure: {}",
                        stderr.lines().next().unwrap_or("(empty stderr)").trim_end(),
                    ),
                };
            }
        }
    }

    if status.success() {
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
        reason = format!("z42vm exited with status {}", status);
    }
    Outcome::Failed { reason }
}

/// Extract the (possibly fully-qualified) thrown type name from z42vm's stderr.
///
/// Best-effort thread-stack snapshot of a hung child process, used by the
/// timeout path in [`run_one`] to give CI logs enough context to diagnose
/// whether the test is deadlocked in z42 user code, a stdlib builtin, or a
/// VM-internal lock.
///
/// Tooling per OS (all stdout captured, stderr discarded for brevity):
///   - macOS:  `sample <pid> 1 -mayDie` — 1 s wall-clock window, then
///             snapshot. Apple-supplied tool, present on every runner.
///   - Linux:  `gdb --batch -p <pid> -ex "thread apply all bt 30"` —
///             attaches via `ptrace`, dumps all threads' top-30 frames.
///             Requires `gdb` + `ptrace_scope` permissive enough to attach
///             to a sibling pid; GitHub Actions ubuntu runners default to
///             `kernel.yama.ptrace_scope=0` so this works without sudo.
///   - Windows: no widely-shipped command-line stack sampler. Skip.
///
/// Returns `None` if the tool is missing or fails — in that case the
/// timeout reason just notes "not captured" rather than failing the test
/// for a different reason. Caps captured output at 32 KB so a runaway
/// trace doesn't blow up the test runner's per-test reason field.
fn capture_stack_trace(pid: u32) -> Option<String> {
    const CAP_BYTES: usize = 32 * 1024;

    let (cmd, args): (&str, Vec<String>) = if cfg!(target_os = "macos") {
        ("sample", vec![pid.to_string(), "1".into(), "-mayDie".into()])
    } else if cfg!(target_os = "linux") {
        (
            "gdb",
            vec![
                "--batch".into(),
                "-p".into(),
                pid.to_string(),
                "-ex".into(),
                "set pagination off".into(),
                "-ex".into(),
                "thread apply all bt 30".into(),
            ],
        )
    } else {
        return None;
    };

    let mut cmd_builder = Command::new(cmd);
    for a in &args { cmd_builder.arg(a); }
    cmd_builder.stdin(Stdio::null());
    cmd_builder.stdout(Stdio::piped());
    cmd_builder.stderr(Stdio::null());

    let output = cmd_builder.output().ok()?;
    if !output.status.success() && output.stdout.is_empty() {
        return None;
    }
    let mut s = String::from_utf8_lossy(&output.stdout).into_owned();
    if s.len() > CAP_BYTES {
        s.truncate(CAP_BYTES);
        s.push_str("\n... (truncated)");
    }
    Some(s)
}

/// z42vm prints uncaught exceptions in one of two shapes (kept consistent with
/// `interp::format_uncaught_exception`):
///   `Error: uncaught exception: <FQ_TYPE>: <msg>`        — string-message form
///   `Error: uncaught exception: <FQ_TYPE>{field=...}`    — object-dump form
///
/// We pull `<FQ_TYPE>` —— the longest prefix of `[A-Za-z0-9_.]` after the
/// `"Error: uncaught exception: "` literal. Returns `None` when the prefix is
/// absent (e.g. VM crashed before exception machinery).
pub fn extract_thrown_type(stderr: &str) -> Option<String> {
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
pub fn type_matches(expected: &str, actual_fq: &str) -> bool {
    if expected == actual_fq { return true; }
    // Trailing-segment match: split on `.`, last piece is the short name.
    let actual_short = actual_fq.rsplit('.').next().unwrap_or(actual_fq);
    !expected.is_empty() && expected == actual_short
}

pub fn extract_exception_msg(stderr: &str) -> String {
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

pub fn resolve_z42vm(override_: Option<&PathBuf>) -> Result<PathBuf> {
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
        let s = "Error: uncaught exception: Std.TestFailure";
        assert_eq!(extract_thrown_type(s), Some("Std.TestFailure".to_string()));
    }

    #[test]
    fn extract_object_dump_form() {
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
        assert!(!type_matches("", "Std.TestFailure"));
        assert!(!type_matches("Foo", ""));
    }

    #[test]
    fn matches_short_against_short() {
        assert!(type_matches("TestFailure", "TestFailure"));
    }

    // ── A3 — semicolon-delimited candidate list parsing ──────────────────

    fn list_match(expected: &str, actual_fq: &str) -> bool {
        expected.split(';').filter(|s| !s.is_empty())
            .any(|c| type_matches(c, actual_fq))
    }

    #[test]
    fn list_single_entry_matches_a2_behavior() {
        assert!(list_match("TestFailure", "Std.TestFailure"));
        assert!(!list_match("TestFailure", "Std.SkipSignal"));
    }

    #[test]
    fn list_inheritance_chain_matches_via_ancestor() {
        assert!(list_match("Exception;Object", "Std.Exception"));
        assert!(list_match("TestFailure;Exception", "Std.Exception"));
    }

    #[test]
    fn list_no_candidate_matches() {
        assert!(!list_match("Foo;Bar;Baz", "Std.TestFailure"));
    }

    #[test]
    fn list_skips_empty_segments() {
        assert!(list_match("Exception;;", "Std.Exception"));
        assert!(!list_match(";;", "Std.Exception"));
    }

    // ── add-test-timeout-attribute (2026-05-30) — compute_budget ──────────

    use super::{compute_budget, DEFAULT_TIMEOUT_SECS, TIMEOUT_HARD_CEILING_SECS};
    use std::time::Duration;

    #[test]
    fn budget_no_override_uses_default_origin() {
        let (budget, origin, clamped) = compute_budget(None);
        assert_eq!(budget, Duration::from_secs(DEFAULT_TIMEOUT_SECS));
        assert_eq!(origin, "runner default");
        assert!(!clamped);
    }

    #[test]
    fn budget_override_within_ceiling_passes_through() {
        let (budget, origin, clamped) = compute_budget(Some(5_000));
        assert_eq!(budget, Duration::from_millis(5_000));
        assert_eq!(origin, "per-method [Timeout]");
        assert!(!clamped);
    }

    #[test]
    fn budget_override_at_ceiling_does_not_clamp() {
        let at_ceiling_ms = (TIMEOUT_HARD_CEILING_SECS * 1_000) as u32;
        let (budget, _, clamped) = compute_budget(Some(at_ceiling_ms));
        assert_eq!(budget, Duration::from_secs(TIMEOUT_HARD_CEILING_SECS));
        assert!(!clamped, "exact-ceiling value is allowed");
    }

    #[test]
    fn budget_override_above_ceiling_is_clamped() {
        // A typo like 60_000_000 ms (~16 h) would otherwise disable the
        // hang detector entirely under the GitHub Actions 6 h job limit.
        let (budget, origin, clamped) = compute_budget(Some(60_000_000));
        assert_eq!(budget, Duration::from_secs(TIMEOUT_HARD_CEILING_SECS));
        assert_eq!(origin, "per-method [Timeout]");
        assert!(clamped);
    }
}
