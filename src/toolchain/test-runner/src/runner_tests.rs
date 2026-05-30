//! Unit tests for `first_user_frame` and `is_framework_frame`.
//!
//! `format_failure_with_stack` needs a `Module + Value` to call — constructing
//! a minimal mock pulls in non-trivial runtime types (TypeDesc, type_registry).
//! The interesting algorithmic logic lives in the two helpers below; tests
//! here exercise them directly. The full glue (`format_failure_with_stack` →
//! Outcome::Failed) is covered end-to-end by the e2e demo
//! `src/libraries/z42.test/tests/failure_location_demo.z42` which proves the
//! runtime → reader → formatter chain actually produces non-None stack data.
//!
//! Phase: surface-test-failure-source-location (2026-05-30).

use super::{first_user_frame, is_framework_frame};

// ── is_framework_frame ────────────────────────────────────────────────

#[test]
fn framework_predicate_matches_std_test_prefix() {
    assert!(is_framework_frame("Std.Test.Assert.Equal"));
    assert!(is_framework_frame("Std.Test.AssertCore.checkEqual"));
    assert!(is_framework_frame("Std.Test.TestIO.captureStdout"));
}

#[test]
fn framework_predicate_matches_assert_substring() {
    // User namespace pattern — design.md Decision 2 calls this intentional
    // over-capture: simple rule > complex regex.
    assert!(is_framework_frame("MyApp.Assert.foo"));
    assert!(is_framework_frame("Tests.Helpers.Assert.bar"));
}

#[test]
fn framework_predicate_rejects_user_frames() {
    assert!(!is_framework_frame("MyTests.test_arithmetic"));
    assert!(!is_framework_frame("App.Pipeline.transform"));
    assert!(!is_framework_frame("Bench.iter"));
    // No `.Assert.` middle segment — only suffix.
    assert!(!is_framework_frame("App.Assertions"));
    // No `Std.Test.` prefix — only `Std.IO.`.
    assert!(!is_framework_frame("Std.IO.Console.WriteLine"));
}

// ── first_user_frame ──────────────────────────────────────────────────

#[test]
fn first_user_frame_returns_none_for_empty_input() {
    assert_eq!(first_user_frame(""), None);
}

#[test]
fn first_user_frame_returns_none_when_all_frames_are_framework() {
    let stack = "\
  at Std.Test.AssertCore.fail (AssertCore.z42:17)
  at Std.Test.Assert.Equal (Assert.z42:38)";
    assert_eq!(first_user_frame(stack), None);
}

#[test]
fn first_user_frame_skips_framework_and_returns_user_location() {
    let stack = "\
  at Std.Test.AssertCore.fail (AssertCore.z42:17)
  at Std.Test.Assert.Equal (Assert.z42:38)
  at MyTests.test_arithmetic (my_test.z42:42)";
    assert_eq!(
        first_user_frame(stack),
        Some("my_test.z42:42".to_string())
    );
}

#[test]
fn first_user_frame_strips_optional_column_suffix() {
    // zbc 1.1+ frames have :col; primary location displays file:line only.
    let stack = "\
  at MyTests.test_x (my_test.z42:42:8)";
    assert_eq!(
        first_user_frame(stack),
        Some("my_test.z42:42".to_string())
    );
}

#[test]
fn first_user_frame_ignores_frames_with_no_parens_locus() {
    // zbc < 1.0 or anonymous closures may lack source info.
    let stack = "\
  at MyTests.opaque
  at MyTests.test_x (my_test.z42:42)";
    assert_eq!(
        first_user_frame(stack),
        Some("my_test.z42:42".to_string())
    );
}

#[test]
fn first_user_frame_skips_line_only_fallback_format() {
    // exception::format_stack_trace emits "(line N, col M)" when file is
    // unknown — we can't produce a clickable file:line, so skip.
    let stack = "\
  at MyTests.test_x (line 42, col 8)";
    assert_eq!(first_user_frame(stack), None);
}

#[test]
fn first_user_frame_picks_first_user_frame_even_with_user_below_framework() {
    // Stack ordering per design: caller-to-throw, throwing function LAST.
    // populate_stack_trace + format_stack_trace render top-of-stack (most
    // recent, framework Assert) at end. We scan top-down so framework is
    // typically nearest to throw; user frames sit below.
    let stack = "\
  at MyTests.test_arithmetic (my_test.z42:42)
  at Std.Test.Assert.Equal (Assert.z42:38)";
    // First non-framework line wins, even if it's not the most-recent frame.
    assert_eq!(
        first_user_frame(stack),
        Some("my_test.z42:42".to_string())
    );
}

#[test]
fn first_user_frame_handles_funcs_with_dots_and_unicode() {
    let stack = "\
  at MyApp.Sub.With.Dots.f (path/to/file.z42:7)";
    assert_eq!(
        first_user_frame(stack),
        Some("path/to/file.z42:7".to_string())
    );
}

#[test]
fn first_user_frame_handles_frame_without_at_prefix_gracefully() {
    // Malformed line in the middle of the stack — keep scanning.
    let stack = "\
GARBAGE LINE
  at MyTests.test_x (my_test.z42:42)";
    assert_eq!(
        first_user_frame(stack),
        Some("my_test.z42:42".to_string())
    );
}
