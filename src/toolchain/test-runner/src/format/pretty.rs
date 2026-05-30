//! Pretty (TTY-friendly, colored) formatter.

use colored::*;

use crate::result::{Summary, TestResult, TestStatus};

pub fn print(module_name: &str, results: &[TestResult]) {
    println!("{}", format!("running {} tests from {}", results.len(), module_name).bold());
    println!();
    for r in results {
        match r.status {
            TestStatus::Passed => println!(
                "  {} {}  ({}ms)", "✓".green().bold(), r.name, r.duration_ms),
            TestStatus::Skipped => println!(
                "  {} {}  ({})", "⊘".yellow().bold(), r.name,
                r.reason.as_deref().unwrap_or("skipped")),
            TestStatus::Failed => {
                // surface-test-failure-source-location (2026-05-30): if a
                // primary user-frame `(file:line)` is available, inline it
                // next to the test name — first thing the eye lands on,
                // clickable in IDE terminals.
                match &r.failure_location {
                    Some(loc) => println!("  {} {}  ({})", "✗".red().bold(), r.name, loc.dimmed()),
                    None => println!("  {} {}", "✗".red().bold(), r.name),
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
