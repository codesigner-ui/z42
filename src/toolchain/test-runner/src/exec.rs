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

/// Per-test wallclock cap. Anything beyond this is treated as a hang —
/// z42vm gets SIGKILL'd and the test reports as `Failed { reason: "timed
/// out after Xs ..." }` instead of locking the whole runner. Generous
/// vs. observed legitimate test durations (most [Test] methods complete
/// in milliseconds; the slowest JIT/crypto/compression ones are ~30 s
/// on cold caches) so this only catches genuine hangs — network tests
/// that race on TCP loopback close handshake (`ws_close` /
/// `ws_ping_pong` under high parallel load on macOS in particular) are
/// the motivating case (Z42-CI-2026-05-29 GREEN-up).
const TEST_TIMEOUT_SECS: u64 = 120;

pub fn run_one(z42vm: &PathBuf, zbc_path: &str, test: &DiscoveredTest) -> Outcome {
    if let Some(reason) = &test.skip_reason {
        return Outcome::Skipped { reason: reason.clone() };
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
    let deadline = Instant::now() + Duration::from_secs(TEST_TIMEOUT_SECS);
    let mut timed_out = false;
    let exit_status = loop {
        match child.try_wait() {
            Ok(Some(s)) => break Some(s),
            Ok(None) => {
                if Instant::now() >= deadline {
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
        let mut reason = format!(
            "timed out after {}s — z42vm killed (likely hung)",
            TEST_TIMEOUT_SECS
        );
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
}
