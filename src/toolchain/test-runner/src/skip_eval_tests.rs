//! Unit tests for `skip_eval::decide_skip` — the conditional `[Skip(...)]`
//! evaluator introduced by add-test-skip-platform-feature-eval (2026-05-30).
//!
//! Matrix design: rows in design.md "Testing Strategy" table. Each test
//! covers one row so a failure points directly at the responsible decision
//! branch.

use super::*;
use crate::discover::DiscoveredTest;
use z42::metadata::TestFlags;

fn env_linux_with_jit() -> SkipEnv {
    let mut features = HashSet::new();
    features.insert("jit".to_string());
    features.insert("interp".to_string());
    features.insert("multithreading".to_string());
    features.insert("filesystem".to_string());
    SkipEnv { current_platform: "linux".to_string(), available_features: features }
}

fn env_ios_no_features() -> SkipEnv {
    SkipEnv { current_platform: "ios".to_string(), available_features: HashSet::new() }
}

/// Build a DiscoveredTest with the given skip-related fields. method_id /
/// method_name / expected_throw / timeout_ms / SHOULD_THROW flag are
/// irrelevant to skip eval and are pinned to neutral values.
fn test_with(
    skipped: bool,
    skip_reason: Option<&str>,
    skip_platform: Option<&str>,
    skip_feature: Option<&str>,
) -> DiscoveredTest<'static> {
    let mut flags = TestFlags::empty();
    if skipped {
        flags |= TestFlags::SKIPPED;
    }
    DiscoveredTest {
        method_id: 0,
        method_name: "t",
        flags,
        is_benchmark: false,
        skip_reason: skip_reason.map(|s| s.to_string()),
        skip_platform: skip_platform.map(|s| s.to_string()),
        skip_feature: skip_feature.map(|s| s.to_string()),
        expected_throw: None,
        timeout_ms: None,
    }
}

// ── case 1: flag off — always None regardless of other fields ─────────

#[test]
fn case_01_flag_off_runs_even_with_skip_fields_set() {
    let t = test_with(false, Some("ignored"), Some("ios"), Some("jit"));
    assert!(decide_skip(&t, &env_linux_with_jit()).is_none());
}

// ── case 2: unconditional skip (no platform, no feature) ──────────────

#[test]
fn case_02_unconditional_skip_returns_reason() {
    let t = test_with(true, Some("broken"), None, None);
    let r = decide_skip(&t, &env_linux_with_jit()).expect("should skip");
    assert_eq!(r, "broken", "plain reason passes through verbatim");
}

#[test]
fn case_02b_unconditional_skip_no_reason_falls_back_to_placeholder() {
    let t = test_with(true, None, None, None);
    assert_eq!(decide_skip(&t, &env_linux_with_jit()).as_deref(), Some("skipped"));
}

// ── case 3 / 4: platform-only ─────────────────────────────────────────

#[test]
fn case_03_platform_match_skips() {
    let t = test_with(true, Some("iOS quirk"), Some("ios"), None);
    let r = decide_skip(&t, &env_ios_no_features()).expect("should skip");
    assert!(r.contains("on ios"), "reason mentions matched platform; got: {r}");
    assert!(r.contains("iOS quirk"), "reason carries user-written reason; got: {r}");
}

#[test]
fn case_04_platform_miss_runs() {
    let t = test_with(true, Some("iOS quirk"), Some("ios"), None);
    assert!(decide_skip(&t, &env_linux_with_jit()).is_none());
}

// ── case 5 / 6: feature-only ──────────────────────────────────────────

#[test]
fn case_05_feature_available_runs() {
    let t = test_with(true, None, None, Some("jit"));
    assert!(decide_skip(&t, &env_linux_with_jit()).is_none());
}

#[test]
fn case_06_feature_unavailable_skips() {
    let t = test_with(true, Some("needs JIT"), None, Some("jit"));
    let r = decide_skip(&t, &env_ios_no_features()).expect("should skip");
    assert!(r.contains("feature \"jit\" unavailable"), "got: {r}");
    assert!(r.contains("needs JIT"), "user reason included; got: {r}");
}

// ── case 7-10: compound OR semantics ──────────────────────────────────

#[test]
fn case_07_compound_both_triggered_skips_with_full_prefix() {
    // ON ios AND jit unavailable.
    let t = test_with(true, Some("not viable"), Some("ios"), Some("jit"));
    let r = decide_skip(&t, &env_ios_no_features()).expect("should skip");
    assert!(r.contains("on ios"), "got: {r}");
    assert!(r.contains("feature \"jit\" unavailable"), "got: {r}");
    assert!(r.contains("not viable"), "got: {r}");
}

#[test]
fn case_08_compound_only_feature_triggers_skips() {
    // ON linux (platform mismatch) but jit unavailable.
    let env = SkipEnv {
        current_platform: "linux".to_string(),
        available_features: HashSet::new(),
    };
    let t = test_with(true, None, Some("ios"), Some("jit"));
    let r = decide_skip(&t, &env).expect("should skip");
    assert!(!r.contains("on linux"), "platform mismatch — must NOT claim platform triggered; got: {r}");
    assert!(r.contains("feature \"jit\" unavailable"), "got: {r}");
}

#[test]
fn case_09_compound_only_platform_triggers_skips() {
    // ON ios (match) but jit available.
    let mut env = env_ios_no_features();
    env.available_features.insert("jit".to_string());
    let t = test_with(true, None, Some("ios"), Some("jit"));
    let r = decide_skip(&t, &env).expect("should skip");
    assert!(r.contains("on ios"), "got: {r}");
    assert!(!r.contains("feature"), "feature was available — must NOT claim feature triggered; got: {r}");
}

#[test]
fn case_10_compound_neither_triggers_runs() {
    // ON linux (mismatch) AND jit available.
    let t = test_with(true, Some("would be skipped on ios w/o jit"), Some("ios"), Some("jit"));
    assert!(decide_skip(&t, &env_linux_with_jit()).is_none());
}

// ── case 11-14: reason string format spot checks ──────────────────────

#[test]
fn case_11_reason_platform_only_has_on_prefix() {
    let t = test_with(true, None, Some("ios"), None);
    let r = decide_skip(&t, &env_ios_no_features()).unwrap();
    assert!(r.starts_with("skipped on ios"), "got: {r}");
}

#[test]
fn case_12_reason_feature_only_has_feature_clause() {
    let t = test_with(true, None, None, Some("jit"));
    let r = decide_skip(&t, &env_ios_no_features()).unwrap();
    assert!(r.starts_with("skipped feature \"jit\" unavailable"), "got: {r}");
}

#[test]
fn case_13_reason_compound_includes_both_clauses_separated_by_semicolon() {
    let t = test_with(true, None, Some("ios"), Some("jit"));
    let r = decide_skip(&t, &env_ios_no_features()).unwrap();
    assert!(r.contains("on ios; feature \"jit\" unavailable"), "got: {r}");
}

#[test]
fn case_14_reason_unconditional_is_verbatim_or_placeholder() {
    let t1 = test_with(true, Some("until next sprint"), None, None);
    assert_eq!(decide_skip(&t1, &env_linux_with_jit()).as_deref(), Some("until next sprint"));

    let t2 = test_with(true, None, None, None);
    assert_eq!(decide_skip(&t2, &env_linux_with_jit()).as_deref(), Some("skipped"));
}

// ── unknown feature: deny-by-default ──────────────────────────────────

#[test]
fn unknown_feature_treated_as_unavailable_and_skips() {
    let t = test_with(true, None, None, Some("quantum_entanglement"));
    let r = decide_skip(&t, &env_linux_with_jit()).expect("unknown feature must skip");
    assert!(r.contains("feature \"quantum_entanglement\" unavailable"), "got: {r}");
}

// ── SkipEnv::detect smoke test ─────────────────────────────────────────

#[test]
fn detect_includes_baseline_features() {
    let env = SkipEnv::detect();
    assert!(env.available_features.contains("interp"));
    assert!(env.available_features.contains("jit"));
    // current_platform should be one of the std::env::consts::OS values.
    assert!(!env.current_platform.is_empty());
}

#[test]
fn with_platform_overrides_detected_value() {
    let env = SkipEnv::detect().with_platform("synthetic-os".to_string());
    assert_eq!(env.current_platform, "synthetic-os");
}
