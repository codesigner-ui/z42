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
                println!("  {} {}", "✗".red().bold(), r.name);
                if let Some(reason) = &r.reason {
                    for line in reason.lines() {
                        println!("      {}", line.red());
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
