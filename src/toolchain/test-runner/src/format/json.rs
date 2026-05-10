//! JSON report formatter.

use anyhow::{Context, Result};
use serde::Serialize;

use crate::result::{Summary, TestResult};

#[derive(Serialize)]
struct JsonReport<'a> {
    tool: &'static str,
    version: &'static str,
    module: &'a str,
    summary: Summary,
    tests: &'a [TestResult],
}

pub fn print(module_name: &str, results: &[TestResult]) -> Result<()> {
    let report = JsonReport {
        tool: "z42-test-runner",
        version: env!("CARGO_PKG_VERSION"),
        module: module_name,
        summary: Summary::from_results(results),
        tests: results,
    };
    let s = serde_json::to_string_pretty(&report)
        .context("serializing JSON report")?;
    println!("{s}");
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::result::TestStatus;

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
    fn json_serialization_round_trip() {
        let results = sample_results();
        let report = JsonReport {
            tool: "z42-test-runner",
            version: "0.1.0",
            module: "demo.zbc",
            summary: Summary::from_results(&results),
            tests: &results,
        };
        let s = serde_json::to_string(&report).unwrap();
        // Spot-check key invariants without snapshotting the whole document.
        assert!(s.contains("\"tool\":\"z42-test-runner\""));
        assert!(s.contains("\"module\":\"demo.zbc\""));
        assert!(s.contains("\"status\":\"passed\""));
        assert!(s.contains("\"status\":\"skipped\""));
        assert!(s.contains("\"status\":\"failed\""));
        assert!(s.contains("\"reason\":\"platform=ios\""));
        assert!(s.contains("\"total\":3"));
        assert!(s.contains("\"passed\":1"));
    }
}
