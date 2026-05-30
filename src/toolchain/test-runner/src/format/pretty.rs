//! Pretty (TTY-friendly, colored) formatter.

use colored::*;

use crate::result::{Summary, TestResult, TestStatus};

pub fn print(module_name: &str, results: &[TestResult]) {
    println!("{}", format!("running {} tests from {}", results.len(), module_name).bold());
    println!();
    for r in results {
        // add-benchmark-runner-dispatch (2026-05-31): tag benchmark entries
        // with a `bench:` prefix so users can grep / visually separate.
        // Execution + outcome semantics are identical to a regular `[Test]`.
        let label = if r.is_benchmark {
            format!("bench:{}", r.name)
        } else {
            r.name.clone()
        };
        match r.status {
            TestStatus::Passed => println!(
                "  {} {}  ({}ms)", "✓".green().bold(), label, r.duration_ms),
            TestStatus::Skipped => println!(
                "  {} {}  ({})", "⊘".yellow().bold(), label,
                r.reason.as_deref().unwrap_or("skipped")),
            TestStatus::Failed => {
                // surface-test-failure-source-location (2026-05-30): if a
                // primary user-frame `(file:line)` is available, inline it
                // next to the test name — first thing the eye lands on,
                // clickable in IDE terminals.
                match &r.failure_location {
                    Some(loc) => println!("  {} {}  ({})", "✗".red().bold(), label, loc.dimmed()),
                    None => println!("  {} {}", "✗".red().bold(), label),
                }
                if let Some(reason) = &r.reason {
                    for line in reason.lines() {
                        println!("      {}", line.red());
                    }
                }
                // Full multi-line stack trace below the reason — dim color so
                // it doesn't compete visually with the main failure summary.
                if let Some(stack) = &r.stack_trace {
                    println!("      {}", "stack:".dimmed());
                    for line in stack.lines() {
                        println!("        {}", line.dimmed());
                    }
                }
            }
        }
    }
    let summary = Summary::from_results(results);
    println!();
    let header = if summary.failed == 0 {
        "ok".green().bold().to_string()
    } else {
        "FAILED".red().bold().to_string()
    };
    println!(
        "result: {}.  {} passed; {} failed; {} skipped",
        header, summary.passed, summary.failed, summary.skipped,
    );
}
