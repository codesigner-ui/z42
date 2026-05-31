//! JUnit XML report formatter.
//!
//! add-junit-xml-formatter (2026-05-31). Emits the de-facto JUnit XML
//! consumed natively by Jenkins (`junit` step), GitLab CI
//! (`artifacts:reports:junit`), CircleCI, and GitHub test reporters.
//!
//! One `.zbc` run == one module == one `<testsuite>`, wrapped in a
//! `<testsuites>` root. `time` attributes are seconds (duration_ms /
//! 1000, 3 decimals). XML is hand-rolled with explicit escaping — no
//! external XML dependency (output side is fully controlled; only 5
//! special chars need handling). Mirrors the no-dep style of tap.rs.

use crate::result::{Summary, TestResult, TestStatus};

pub fn print(module_name: &str, results: &[TestResult]) {
    let summary = Summary::from_results(results);
    let suite_time = secs(summary.duration_ms);

    println!("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    println!(
        "<testsuites tests=\"{}\" failures=\"{}\" skipped=\"{}\" time=\"{}\">",
        summary.total, summary.failed, summary.skipped, suite_time
    );
    println!(
        "  <testsuite name=\"{}\" tests=\"{}\" failures=\"{}\" skipped=\"{}\" time=\"{}\">",
        xml_attr(module_name), summary.total, summary.failed, summary.skipped, suite_time
    );

    for r in results {
        let time = secs(r.duration_ms);
        let name = xml_attr(&r.name);
        let classname = xml_attr(module_name);
        match r.status {
            TestStatus::Passed => {
                println!(
                    "    <testcase name=\"{name}\" classname=\"{classname}\" time=\"{time}\"/>"
                );
            }
            TestStatus::Skipped => {
                let msg = xml_attr(r.reason.as_deref().unwrap_or("skipped"));
                println!(
                    "    <testcase name=\"{name}\" classname=\"{classname}\" time=\"{time}\">"
                );
                println!("      <skipped message=\"{msg}\"/>");
                println!("    </testcase>");
            }
            TestStatus::Failed => {
                let reason = r.reason.as_deref().unwrap_or("test failed");
                // JUnit `message` attr = concise first line; body = full
                // detail (reason + stack trace if present).
                let first_line = reason.lines().next().unwrap_or(reason);
                let mut body = String::from(reason);
                if let Some(stack) = &r.stack_trace {
                    body.push_str("\n");
                    body.push_str(stack);
                }
                println!(
                    "    <testcase name=\"{name}\" classname=\"{classname}\" time=\"{time}\">"
                );
                println!(
                    "      <failure message=\"{}\">{}</failure>",
                    xml_attr(first_line),
                    xml_text(&body)
                );
                println!("    </testcase>");
            }
        }
    }

    println!("  </testsuite>");
    println!("</testsuites>");
}

/// duration_ms → seconds string with 3 decimals (JUnit `time` convention).
fn secs(duration_ms: u64) -> String {
    format!("{:.3}", duration_ms as f64 / 1000.0)
}

/// Escape for an XML attribute value (inside double quotes). Handles the
/// five predefined entities; `"` must be escaped since we quote with `"`.
fn xml_attr(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\'' => out.push_str("&apos;"),
            _ => out.push(c),
        }
    }
    out
}

/// Escape for XML element text content. `<` and `&` are mandatory; `>` is
/// escaped defensively (avoids the `]]>` edge in CDATA-free output).
fn xml_text(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            _ => out.push(c),
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn passed(name: &str, ms: u64) -> TestResult {
        TestResult {
            name: name.into(), status: TestStatus::Passed, duration_ms: ms,
            reason: None, failure_location: None, stack_trace: None,
            is_benchmark: false, bench_stats: None,
        }
    }
    fn failed(name: &str, reason: &str) -> TestResult {
        TestResult {
            name: name.into(), status: TestStatus::Failed, duration_ms: 0,
            reason: Some(reason.into()), failure_location: None, stack_trace: None,
            is_benchmark: false, bench_stats: None,
        }
    }
    fn skipped(name: &str, reason: &str) -> TestResult {
        TestResult {
            name: name.into(), status: TestStatus::Skipped, duration_ms: 0,
            reason: Some(reason.into()), failure_location: None, stack_trace: None,
            is_benchmark: false, bench_stats: None,
        }
    }

    // Re-implement print into a String for assertions (print() writes stdout).
    fn render(module: &str, results: &[TestResult]) -> String {
        let summary = Summary::from_results(results);
        let suite_time = secs(summary.duration_ms);
        let mut o = String::new();
        o.push_str("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        o.push_str(&format!(
            "<testsuites tests=\"{}\" failures=\"{}\" skipped=\"{}\" time=\"{}\">\n",
            summary.total, summary.failed, summary.skipped, suite_time));
        o.push_str(&format!(
            "  <testsuite name=\"{}\" tests=\"{}\" failures=\"{}\" skipped=\"{}\" time=\"{}\">\n",
            xml_attr(module), summary.total, summary.failed, summary.skipped, suite_time));
        for r in results {
            let time = secs(r.duration_ms);
            let name = xml_attr(&r.name);
            let classname = xml_attr(module);
            match r.status {
                TestStatus::Passed => o.push_str(&format!(
                    "    <testcase name=\"{name}\" classname=\"{classname}\" time=\"{time}\"/>\n")),
                TestStatus::Skipped => {
                    let msg = xml_attr(r.reason.as_deref().unwrap_or("skipped"));
                    o.push_str(&format!("    <testcase name=\"{name}\" classname=\"{classname}\" time=\"{time}\">\n"));
                    o.push_str(&format!("      <skipped message=\"{msg}\"/>\n"));
                    o.push_str("    </testcase>\n");
                }
                TestStatus::Failed => {
                    let reason = r.reason.as_deref().unwrap_or("test failed");
                    let first_line = reason.lines().next().unwrap_or(reason);
                    let mut body = String::from(reason);
                    if let Some(stack) = &r.stack_trace { body.push('\n'); body.push_str(stack); }
                    o.push_str(&format!("    <testcase name=\"{name}\" classname=\"{classname}\" time=\"{time}\">\n"));
                    o.push_str(&format!("      <failure message=\"{}\">{}</failure>\n",
                        xml_attr(first_line), xml_text(&body)));
                    o.push_str("    </testcase>\n");
                }
            }
        }
        o.push_str("  </testsuite>\n</testsuites>\n");
        o
    }

    #[test]
    fn junit_skeleton_passed_test() {
        let xml = render("MyMod", &[passed("MyMod.test_a", 12)]);
        assert!(xml.contains("<?xml version=\"1.0\" encoding=\"UTF-8\"?>"));
        assert!(xml.contains("<testcase name=\"MyMod.test_a\" classname=\"MyMod\" time=\"0.012\"/>"),
            "self-closing passed testcase; got:\n{xml}");
    }

    #[test]
    fn junit_failed_test_has_failure_element() {
        let xml = render("M", &[failed("M.t", "values not equal\nextra detail")]);
        assert!(xml.contains("<failure message=\"values not equal\">"),
            "message = first line only; got:\n{xml}");
        assert!(xml.contains("extra detail"), "body carries full reason; got:\n{xml}");
    }

    #[test]
    fn junit_skipped_test_has_skipped_element() {
        let xml = render("M", &[skipped("M.t", "platform=ios")]);
        assert!(xml.contains("<skipped message=\"platform=ios\"/>"), "got:\n{xml}");
    }

    #[test]
    fn junit_escapes_xml_special_chars() {
        let xml = render("M", &[failed("M.t", "expected <a> & \"b\"")]);
        assert!(xml.contains("&lt;a&gt;"), "angle brackets escaped; got:\n{xml}");
        assert!(xml.contains("&amp;"), "ampersand escaped; got:\n{xml}");
        // Inside the message attr, the quote must be escaped too.
        assert!(xml.contains("&quot;b&quot;"), "quotes escaped in attr; got:\n{xml}");
    }

    #[test]
    fn junit_testsuites_counts_match() {
        let xml = render("M", &[
            passed("M.p", 5),
            failed("M.f", "boom"),
            skipped("M.s", "later"),
        ]);
        assert!(xml.contains("<testsuites tests=\"3\" failures=\"1\" skipped=\"1\""),
            "root counts; got:\n{xml}");
        assert!(xml.contains("<testsuite name=\"M\" tests=\"3\" failures=\"1\" skipped=\"1\""),
            "suite counts; got:\n{xml}");
    }

    #[test]
    fn junit_failed_includes_stack_when_present() {
        let mut f = failed("M.t", "assert failed");
        f.stack_trace = Some("  at M.t (m.z42:7)".into());
        let xml = render("M", &[f]);
        assert!(xml.contains("at M.t (m.z42:7)"), "stack appended to body; got:\n{xml}");
    }
}
