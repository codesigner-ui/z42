//! Conditional `[Skip(platform:)] / [Skip(feature:)]` evaluation.
//!
//! Phase: add-test-skip-platform-feature-eval (2026-05-30).
//!
//! Before this module, the runner unconditionally skipped any test with the
//! `SKIPPED` flag — `[Skip(platform: "ios")]` skipped on every host, not just
//! iOS, contradicting the documented "skip only when platform matches"
//! semantic. This module centralises the evaluation logic so the in-process,
//! legacy-subprocess, and parallel-subprocess execution paths share one
//! authority on "should this test run here?".
//!
//! Design rationale: see `docs/spec/archive/2026-05-30-add-test-skip-
//! platform-feature-eval/design.md`. Highlights:
//! - **Platform source**: `std::env::consts::OS` (same const z42 stdlib reads
//!   via `Std.Platform.OS()`), CLI `--platform <NAME>` + env
//!   `Z42_TEST_PLATFORM` override
//! - **Compound semantic**: OR — `[Skip(platform: ios, feature: jit)]` skips
//!   when ON ios OR jit unavailable, not AND
//! - **Unknown features**: deny-by-default — typo `[Skip(feature:
//!   "multi-threading")]` (intended `multithreading`) skips instead of
//!   running an inapplicable test
//! - **Reason field**: enriched with the triggered condition for human
//!   readability — "skipped on ios: WebGL bug" beats raw user text

use std::collections::HashSet;
use std::sync::Mutex;
use std::sync::OnceLock;

use crate::discover::DiscoveredTest;
use z42::metadata::TestFlags;

/// Host capability snapshot — current platform name + set of available
/// runtime feature flags. Constructed once at runner startup (with optional
/// CLI / env overrides) and passed by reference into every per-test skip
/// evaluation.
#[derive(Debug, Clone)]
pub struct SkipEnv {
    pub current_platform: String,
    pub available_features: HashSet<String>,
}

impl SkipEnv {
    /// Build from compile-time platform constants + cfg-driven feature flags.
    /// Callers apply CLI / env overrides on top via `with_platform`.
    pub fn detect() -> Self {
        let mut features: HashSet<String> = HashSet::new();
        // Always-on capabilities: interp and JIT are both compiled into z42vm;
        // the choice between them is per-method (ExecMode), not a build-time
        // capability, so neither is ever "unavailable" in the current shipping
        // binary.
        features.insert("interp".to_string());
        features.insert("jit".to_string());

        // wasm32 builds run single-threaded inside the sandbox and lack a
        // host filesystem; native builds get both. If we add other constrained
        // targets (embedded, browser-extension) the cfg list grows here.
        #[cfg(not(target_arch = "wasm32"))]
        {
            features.insert("multithreading".to_string());
            features.insert("filesystem".to_string());
        }

        Self {
            current_platform: std::env::consts::OS.to_string(),
            available_features: features,
        }
    }

    /// Override the auto-detected platform (CLI `--platform` or env
    /// `Z42_TEST_PLATFORM`). Used to verify `[Skip(platform:)]` gating across
    /// hosts on a single machine.
    pub fn with_platform(mut self, p: String) -> Self {
        self.current_platform = p;
        self
    }
}

/// Decide whether `test` should be skipped under the given environment.
///
/// Returns `Some(reason)` when the test should be skipped (caller wraps in
/// `Outcome::Skipped`); `None` when the test should run normally.
///
/// Algorithm — three-way decision based on (platform-match, feature-unavail):
/// - flag off → always `None` (test runs)
/// - flag on, no conditions → `Some` (unconditional skip, R1.A behavior)
/// - flag on, platform only → `Some` iff platform matches current host
/// - flag on, feature only → `Some` iff feature is unavailable
/// - flag on, both → `Some` iff EITHER condition triggers (OR semantic)
pub fn decide_skip(test: &DiscoveredTest, env: &SkipEnv) -> Option<String> {
    if !test.flags.contains(TestFlags::SKIPPED) {
        return None;
    }

    let plat_match = test
        .skip_platform
        .as_ref()
        .map(|p| p == &env.current_platform);

    let feat_unavail = test
        .skip_feature
        .as_ref()
        .map(|f| !is_feature_available(f, env));

    let triggered = match (plat_match, feat_unavail) {
        (None, None) => true, // unconditional [Skip] — R1.A behavior
        (Some(p), None) => p,
        (None, Some(f)) => f,
        (Some(p), Some(f)) => p || f, // compound: OR
    };

    if !triggered {
        return None;
    }

    Some(format_reason(
        test,
        env,
        plat_match.unwrap_or(false),
        feat_unavail.unwrap_or(false),
    ))
}

/// Check feature availability with deny-by-default for unknown names.
/// Emits a one-time stderr `note:` per unknown name across the whole run
/// (dedupe via the registry below) so test-suite logs don't drown.
fn is_feature_available(name: &str, env: &SkipEnv) -> bool {
    if env.available_features.contains(name) {
        return true;
    }
    if !is_known_feature(name) {
        warn_unknown_feature_once(name);
    }
    false
}

/// The static set of feature names this runner recognises. `available_features`
/// is the *subset* available on the current host; this set is the *vocabulary*
/// used to distinguish "known but unavailable here" from "typo / never heard
/// of it".
fn is_known_feature(name: &str) -> bool {
    matches!(name, "interp" | "jit" | "multithreading" | "filesystem")
}

fn warn_unknown_feature_once(name: &str) {
    static SEEN: OnceLock<Mutex<HashSet<String>>> = OnceLock::new();
    let seen = SEEN.get_or_init(|| Mutex::new(HashSet::new()));
    let mut guard = seen.lock().unwrap();
    if guard.insert(name.to_string()) {
        eprintln!(
            "note: unknown feature {:?} — treating as unavailable (test will skip)",
            name
        );
    }
}

/// Synthesize the "why skipped" human message. The triggered condition is
/// always prefixed so failure-triage on first read tells you not just "this
/// skipped" but "this skipped *because* of X".
fn format_reason(
    test: &DiscoveredTest,
    env: &SkipEnv,
    plat_triggered: bool,
    feat_triggered: bool,
) -> String {
    let mut prefix = String::new();

    if plat_triggered {
        prefix.push_str(&format!("on {}", env.current_platform));
    }
    if feat_triggered {
        if !prefix.is_empty() {
            prefix.push_str("; ");
        }
        prefix.push_str(&format!(
            "feature {:?} unavailable",
            test.skip_feature.as_deref().unwrap_or("")
        ));
    }

    match (prefix.is_empty(), test.skip_reason.as_ref()) {
        (true, Some(r)) if !r.is_empty() => r.clone(),       // plain reason only
        (true, _) => "skipped".to_string(),                   // bare [Skip]
        (false, Some(r)) if !r.is_empty() => format!("skipped {prefix}: {r}"),
        (false, _) => format!("skipped {prefix}"),
    }
}

#[cfg(test)]
#[path = "skip_eval_tests.rs"]
mod skip_eval_tests;
