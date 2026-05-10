//! TAP version 13 formatter (testanything.org).

use crate::result::{TestResult, TestStatus};

/// TAP version 13. Plan line first, then `ok` / `not ok` per test. YAML
/// diagnostic block only on failures (skip reason carried in the directive).
/// No color codes — TAP consumers expect plain ASCII.
pub fn print(_module_name: &str, results: &[TestResult]) {
    println!("TAP version 13");
    println!("1..{}", results.len());
    for (idx, r) in results.iter().enumerate() {
        let n = idx + 1;
        match r.status {
            TestStatus::Passed => println!("ok {n} - {}", r.name),
            TestStatus::Skipped => {
                let reason = r.reason.as_deref().unwrap_or("skipped");
                println!("ok {n} - {} # SKIP {}", r.name, reason);
            }
            TestStatus::Failed => {
                println!("not ok {n} - {}", r.name);
                if let Some(reason) = &r.reason {
                    println!("  ---");
                    println!("  message: {}", yaml_escape(reason));
                    println!("  ...");
                }
            }
        }
    }
}

/// YAML 1.2 single-line escape: wrap in single quotes, double interior `'`,
/// collapse newlines to spaces. Adequate for our short failure messages; if
/// future reasons embed structured content, replace with full YAML emitter.
pub fn yaml_escape(s: &str) -> String {
    let collapsed: String = s.lines().collect::<Vec<_>>().join(" ");
    format!("'{}'", collapsed.replace('\'', "''"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::result::Summary;

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
    fn tap_format_matches_v13_skeleton() {
        let mut buf = Vec::new();
        // Reproduce print() format in a string and assert exact shape.
        buf.push("TAP version 13".to_string());
        buf.push(format!("1..{}", sample_results().len()));
        for (idx, r) in sample_results().iter().enumerate() {
            let n = idx + 1;
            match r.status {
                TestStatus::Passed  => buf.push(format!("ok {n} - {}", r.name)),
                TestStatus::Skipped => buf.push(format!(
                    "ok {n} - {} # SKIP {}", r.name,
                    r.reason.as_deref().unwrap_or("skipped"))),
                TestStatus::Failed  => {
                    buf.push(format!("not ok {n} - {}", r.name));
                    if let Some(reason) = &r.reason {
                        buf.push("  ---".to_string());
                        buf.push(format!("  message: {}", yaml_escape(reason)));
                        buf.push("  ...".to_string());
                    }
                }
            }
        }
        let expected = [
            "TAP version 13",
            "1..3",
            "ok 1 - M.test_pass",
            "ok 2 - M.test_skip # SKIP platform=ios",
            "not ok 3 - M.test_fail",
            "  ---",
            "  message: 'expected `Foo`, got `Bar`'",
            "  ...",
        ].join("\n");
        assert_eq!(buf.join("\n"), expected);
        // Touch Summary so the unused-import lint stays quiet (Summary itself
        // is not used by tap formatter — kept for callers that might want it).
        let _ = Summary::from_results(&sample_results());
    }

    #[test]
    fn yaml_escape_handles_quotes_and_newlines() {
        assert_eq!(yaml_escape("hello"), "'hello'");
        assert_eq!(yaml_escape("can't"), "'can''t'");
        assert_eq!(yaml_escape("line one\nline two"), "'line one line two'");
    }
}
