//! Test execution result types.
//!
//! Phase: rewrite-z42-test-runner-compile-time S1 (2026-05-10) — extracted
//! from monolithic `main.rs` to modularize.

use serde::Serialize;

#[derive(Copy, Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum TestStatus { Passed, Failed, Skipped }

#[derive(Debug, Serialize)]
pub struct TestResult {
    pub name: String,
    pub status: TestStatus,
    /// Wallclock duration in milliseconds. Always present for `passed` and
    /// `failed`; `0` (and meaningless) for synthesized `skipped` results that
    /// short-circuit before z42vm is spawned.
    pub duration_ms: u64,
    /// Failure message or skip rationale. `None` for `passed`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

impl TestResult {
    pub fn from_outcome(name: String, outcome: Outcome) -> Self {
        match outcome {
            Outcome::Passed { duration_ms } => Self {
                name, status: TestStatus::Passed, duration_ms, reason: None,
            },
            Outcome::Skipped { reason } => Self {
                name, status: TestStatus::Skipped, duration_ms: 0, reason: Some(reason),
            },
            Outcome::Failed { reason } => Self {
                name, status: TestStatus::Failed, duration_ms: 0, reason: Some(reason),
            },
        }
    }
}

#[derive(Default, Serialize)]
pub struct Summary {
    pub total: usize,
    pub passed: usize,
    pub failed: usize,
    pub skipped: usize,
    pub duration_ms: u64,
}

impl Summary {
    pub fn from_results(results: &[TestResult]) -> Self {
        let mut s = Self::default();
        for r in results {
            s.total += 1;
            s.duration_ms += r.duration_ms;
            match r.status {
                TestStatus::Passed  => s.passed  += 1,
                TestStatus::Failed  => s.failed  += 1,
                TestStatus::Skipped => s.skipped += 1,
            }
        }
        s
    }
}

/// Internal outcome type produced by the executor; converted to public
/// [`TestResult`] via [`TestResult::from_outcome`].
pub enum Outcome {
    Passed { duration_ms: u64 },
    Failed { reason: String },
    Skipped { reason: String },
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_results() -> Vec<TestResult> {
        vec![
            TestResult {
                name: "M.test_pass".into(), status: TestStatus::Passed,
                duration_ms: 12, reason: None,
            },
            TestResult {
                name: "M.test_skip".into(), status: TestStatus::Skipped,
                duration_ms: 0, reason: Some("platform=ios".into()),
            },
            TestResult {
                name: "M.test_fail".into(), status: TestStatus::Failed,
                duration_ms: 7,
                reason: Some("expected `Foo`, got `Bar`".into()),
            },
        ]
    }

    #[test]
    fn summary_aggregates_correctly() {
        let summary = Summary::from_results(&sample_results());
        assert_eq!(summary.total, 3);
        assert_eq!(summary.passed, 1);
        assert_eq!(summary.failed, 1);
        assert_eq!(summary.skipped, 1);
        assert_eq!(summary.duration_ms, 12 + 0 + 7);
    }

    #[test]
    fn json_passed_omits_reason_field() {
        let r = TestResult {
            name: "M.t".into(), status: TestStatus::Passed,
            duration_ms: 5, reason: None,
        };
        let s = serde_json::to_string(&r).unwrap();
        assert!(!s.contains("\"reason\""),
            "passed test should not serialize a `reason` field");
    }
}
