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
    /// Failure message or skip rationale. `None` for `passed`. Backward-
    /// compatible content — pre-2026-05-30 CI scripts that grep `reason`
    /// continue to work unchanged.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    /// surface-test-failure-source-location (2026-05-30): first non-framework
    /// stack frame's `<file>:<line>` for failed tests, extracted from the
    /// thrown Exception's `StackTrace` field by `runner::first_user_frame`.
    /// `None` when the throw site had no populated stack, no z42-side debug
    /// info, or the trace contained only Std.Test / Assert framework frames.
    /// IDE / CI tooling can jump to source via this field.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub failure_location: Option<String>,
    /// surface-test-failure-source-location (2026-05-30): full multi-line
    /// stack trace as produced by `z42::exception::format_stack_trace`. Not
    /// filtered for framework frames — deep-debugging Assert-internal bugs
    /// needs the complete view. `None` follows the same rules as
    /// `failure_location`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stack_trace: Option<String>,
    /// add-benchmark-runner-dispatch (2026-05-31): true for entries that
    /// originated from `TestEntryKind::Benchmark`. Execution path is
    /// identical to a regular `[Test]` (zero-arg call, same Outcome
    /// semantics); this flag only changes display labeling — pretty
    /// formatter prefixes the test name with `bench:`, JSON consumers
    /// can filter / group benchmark results separately.
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub is_benchmark: bool,
    /// capture-benchmark-stats-in-testresult (2026-05-31): structured
    /// stats parsed out of the user's `Bencher.printSummary(label)` line
    /// in subprocess-captured stdout. `None` when the test isn't a
    /// benchmark, didn't call printSummary, or runs in-process (stdout
    /// not captured today). CI perf-tracking tools consume this to detect
    /// regressions without parsing log output.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bench_stats: Option<BenchStats>,
}

/// capture-benchmark-stats-in-testresult (2026-05-31): structured form of
/// a single `Bencher.printSummary` line. All times in nanoseconds.
///
/// Source format (Bencher.z42:82-84):
/// `bench[<label>] min=<n>ns median=<n>ns max=<n>ns samples=<n>`
///
/// `total_ns` is not currently emitted by printSummary; reserved 0 until
/// a future Bencher format upgrade fills it.
#[derive(Debug, Clone, Serialize)]
pub struct BenchStats {
    pub label: String,
    pub min_ns: i64,
    pub median_ns: i64,
    pub max_ns: i64,
    pub samples: i64,
    pub total_ns: i64,
}

impl TestResult {
    pub fn from_outcome(name: String, outcome: Outcome, is_benchmark: bool) -> Self {
        match outcome {
            Outcome::Passed { duration_ms } => Self {
                name, status: TestStatus::Passed, duration_ms,
                reason: None, failure_location: None, stack_trace: None,
                is_benchmark, bench_stats: None,
            },
            Outcome::Skipped { reason } => Self {
                name, status: TestStatus::Skipped, duration_ms: 0,
                reason: Some(reason), failure_location: None, stack_trace: None,
                is_benchmark, bench_stats: None,
            },
            Outcome::Failed { reason, location, stack_trace } => Self {
                name, status: TestStatus::Failed, duration_ms: 0,
                reason: Some(reason),
                failure_location: location,
                stack_trace,
                is_benchmark, bench_stats: None,
            },
        }
    }

    /// capture-benchmark-stats-in-testresult (2026-05-31): attach parsed
    /// `BenchStats` after constructing a TestResult. Caller side (exec.rs)
    /// chains this when subprocess stdout contained a parseable
    /// `bench[label] min=…` line.
    pub fn with_bench_stats(mut self, stats: Option<BenchStats>) -> Self {
        self.bench_stats = stats;
        self
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
    /// surface-test-failure-source-location (2026-05-30): `location` carries
    /// the first non-framework stack frame for IDE jump-to-source;
    /// `stack_trace` carries the full multi-line trace for the formatter to
    /// surface verbatim. Both are `None` for non-z42-side failures (e.g.
    /// Setup/Teardown VM errors that don't go through a thrown Exception).
    Failed {
        reason: String,
        location: Option<String>,
        stack_trace: Option<String>,
    },
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
                failure_location: None, stack_trace: None,
                is_benchmark: false, bench_stats: None,
            },
            TestResult {
                name: "M.test_skip".into(), status: TestStatus::Skipped,
                duration_ms: 0, reason: Some("platform=ios".into()),
                failure_location: None, stack_trace: None,
                is_benchmark: false, bench_stats: None,
            },
            TestResult {
                name: "M.test_fail".into(), status: TestStatus::Failed,
                duration_ms: 7,
                reason: Some("expected `Foo`, got `Bar`".into()),
                failure_location: None, stack_trace: None,
                is_benchmark: false, bench_stats: None,
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
            failure_location: None, stack_trace: None,
            is_benchmark: false, bench_stats: None,
        };
        let s = serde_json::to_string(&r).unwrap();
        assert!(!s.contains("\"reason\""),
            "passed test should not serialize a `reason` field");
        assert!(!s.contains("\"failure_location\""),
            "passed test should not serialize `failure_location`");
        assert!(!s.contains("\"stack_trace\""),
            "passed test should not serialize `stack_trace`");
        assert!(!s.contains("\"is_benchmark\""),
            "is_benchmark=false should be omitted from JSON");
        assert!(!s.contains("\"bench_stats\""),
            "bench_stats=None should be omitted from JSON");
    }

    #[test]
    fn json_failed_includes_new_fields_when_present() {
        let r = TestResult {
            name: "M.t".into(), status: TestStatus::Failed,
            duration_ms: 3,
            reason: Some("values not equal".into()),
            failure_location: Some("my_test.z42:42".into()),
            stack_trace: Some("  at M.t (my_test.z42:42)".into()),
            is_benchmark: false, bench_stats: None,
        };
        let s = serde_json::to_string(&r).unwrap();
        assert!(s.contains("\"failure_location\":\"my_test.z42:42\""),
            "got: {s}");
        assert!(s.contains("\"stack_trace\":\"  at M.t (my_test.z42:42)\""),
            "got: {s}");
    }

    #[test]
    fn json_benchmark_includes_bench_stats_when_present() {
        let r = TestResult {
            name: "M.bench_addition".into(), status: TestStatus::Passed,
            duration_ms: 50, reason: None,
            failure_location: None, stack_trace: None,
            is_benchmark: true,
            bench_stats: Some(BenchStats {
                label: "addition".into(),
                min_ns: 3875,
                median_ns: 3958,
                max_ns: 4666,
                samples: 5,
                total_ns: 0,
            }),
        };
        let s = serde_json::to_string(&r).unwrap();
        assert!(s.contains("\"bench_stats\""), "bench_stats present; got: {s}");
        assert!(s.contains("\"label\":\"addition\""), "got: {s}");
        assert!(s.contains("\"min_ns\":3875"), "got: {s}");
        assert!(s.contains("\"median_ns\":3958"), "got: {s}");
        assert!(s.contains("\"max_ns\":4666"), "got: {s}");
        assert!(s.contains("\"samples\":5"), "got: {s}");
    }

    #[test]
    fn with_bench_stats_attaches_field() {
        let r = TestResult::from_outcome(
            "M.bench".into(),
            Outcome::Passed { duration_ms: 10 },
            true,
        ).with_bench_stats(Some(BenchStats {
            label: "x".into(),
            min_ns: 1, median_ns: 2, max_ns: 3, samples: 4, total_ns: 5,
        }));
        assert!(r.bench_stats.is_some());
        assert_eq!(r.bench_stats.as_ref().unwrap().label, "x");
    }

    #[test]
    fn json_benchmark_emits_is_benchmark_true() {
        let r = TestResult {
            name: "M.bench".into(), status: TestStatus::Passed,
            duration_ms: 42, reason: None,
            failure_location: None, stack_trace: None,
            is_benchmark: true, bench_stats: None,
        };
        let s = serde_json::to_string(&r).unwrap();
        assert!(s.contains("\"is_benchmark\":true"),
            "is_benchmark=true should serialize; got: {s}");
    }
}
