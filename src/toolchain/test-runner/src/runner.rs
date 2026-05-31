//! In-process test execution.
//!
//! Phase: rewrite-z42-test-runner-compile-time S3 (2026-05-10) — replaces
//! `exec.rs` subprocess fork with direct `interp::run_outcome` calls.
//!
//! Per-test cycle (shared VmContext, isolated static fields):
//!   1. `init_static_fields` — resets static state + runs all `__static_init__`
//!   2. Setup methods (TIDX kind=Setup) sequentially via `interp::run_outcome`
//!   3. Test body via `interp::run_outcome`
//!   4. Teardown methods (always, even if test failed)
//!   5. Classify exception value (if any) into Outcome
//!
//! Exception classification (mirrors S1 subprocess behavior):
//!   - Std.SkipSignal → Outcome::Skipped
//!   - Std.TestFailure → Outcome::Failed
//!   - Other thrown value → Outcome::Failed (with type info)
//!   - [ShouldThrow<E>] expectation → invert Pass/Fail by type match

use anyhow::Result;
use std::time::Instant;

use z42::interp::{self, ExecOutcome};
use z42::metadata::{Function, Module, TestEntry, TestEntryKind, Value};
use z42::vm_context::VmContext;

use crate::bootstrap::LoadedRunner;
use crate::discover::DiscoveredTest;
use crate::result::Outcome;
use crate::skip_eval::{decide_skip, SkipEnv};

pub fn run_one(
    loaded: &mut LoadedRunner,
    test: &DiscoveredTest<'_>,
    skip_env: &SkipEnv,
) -> (Outcome, Option<crate::result::BenchStats>) {
    // bench-stats-in-process-capture (2026-05-31): mirror exec::run_one's
    // tuple return so the in-process path can hand BenchStats back to
    // main.rs for TestResult.bench_stats. The Option is always None for
    // non-benchmark entries (we don't install the sink) and may be Some
    // for benchmarks whose body called `Bencher.printSummary(label)`.

    // add-test-skip-platform-feature-eval (2026-05-30): consult the
    // conditional evaluator instead of unconditionally skipping any flagged
    // test. `decide_skip` honors `[Skip(platform: …)]` / `[Skip(feature: …)]`
    // against the current host; returns None when the test should run.
    if let Some(reason) = decide_skip(test, skip_env) {
        return (Outcome::Skipped { reason }, None);
    }

    let start = Instant::now();

    // 1. Per-test isolation: clear static fields + re-run all __static_init__.
    if let Err(e) = interp::init_static_fields(&loaded.ctx, &loaded.ctx.module().unwrap()) {
        return (Outcome::Failed {
            reason: format!("static init error: {e}"),
            location: None,
            stack_trace: None,
        }, None);
    }

    // 2. Setup methods (sequential; first failure short-circuits to test fail).
    let setup_names = collect_kind_names(&loaded.test_index, &loaded.user_func_names, TestEntryKind::Setup);
    for setup in &setup_names {
        if let Some(Outcome::Failed { reason, location, stack_trace }) =
            exec_named(&loaded.ctx.module().unwrap(), &loaded.ctx, setup, "Setup")
        {
            run_teardowns(loaded);
            return (Outcome::Failed { reason, location, stack_trace }, None);
        }
    }

    // 3. Test body — capture stdout for benchmarks so Bencher.printSummary
    //    output can be parsed into TestResult.bench_stats. The captured
    //    bytes are re-emitted to process stdout after capture so the user
    //    still sees the output in their terminal (matching subprocess
    //    behavior where stdout is captured + propagated by the parent).
    let (outcome, bench_stats) = if test.is_benchmark {
        z42::corelib::io::push_stdout_sink();
        let outcome = exec_test_body(loaded, test);
        let captured = z42::corelib::io::take_stdout_sink();
        // Re-emit so terminal/CI logs still see the bench output. Bytes
        // are preserved as-is (the user's trailing \n is already in the
        // buffer from Console.WriteLine).
        use std::io::Write as _;
        let _ = std::io::stdout().write_all(&captured);
        let stats = crate::exec::extract_bench_stats_from_stdout(
            &String::from_utf8_lossy(&captured),
        );
        (outcome, stats)
    } else {
        (exec_test_body(loaded, test), None)
    };

    // 4. Teardown methods (always, even on test fail — mirrors xUnit).
    run_teardowns(loaded);

    // 5. Wallclock includes Setup/Teardown time (TODO: split if needed for
    //    accurate Bencher reporting).
    let duration_ms = start.elapsed().as_millis() as u64;

    let final_outcome = match outcome {
        Outcome::Passed { .. } => Outcome::Passed { duration_ms },
        other => other, // Failed / Skipped already carries reason
    };
    (final_outcome, bench_stats)
}

/// Run a Setup / Teardown function by name. Returns:
///   - `None` if the function doesn't exist (mismatch between TIDX and module)
///   - `Some(Outcome::Failed)` if it threw or errored
///   - `Some(Outcome::Passed)` on clean return
fn exec_named(module: &Module, ctx: &VmContext, name: &str, role: &str) -> Option<Outcome> {
    let func = module.functions.iter().find(|f| f.name == name)?;
    Some(exec_one(module, ctx, func, role))
}

fn exec_one(module: &Module, ctx: &VmContext, func: &Function, role: &str) -> Outcome {
    let args: &[Value] = &[];
    match interp::run_outcome(ctx, module, func, args) {
        Ok(ExecOutcome::Returned(_)) => Outcome::Passed { duration_ms: 0 },
        Ok(ExecOutcome::Thrown(val)) => {
            // surface-test-failure-source-location (2026-05-30): even Setup/
            // Teardown failures get the full StackTrace surfaced — same value
            // to triage.
            let details = format_failure_with_stack(&val, module);
            Outcome::Failed {
                reason: format!("{role}: uncaught exception: {}", details.message),
                location: details.primary_location,
                stack_trace: details.stack_trace,
            }
        }
        Err(e) => Outcome::Failed {
            // VM error path — Rust-side error, no z42 exception value, no
            // populated StackTrace to surface.
            reason: format!("{role}: VM error: {e}"),
            location: None,
            stack_trace: None,
        },
    }
}

fn exec_test_body(loaded: &LoadedRunner, test: &DiscoveredTest<'_>) -> Outcome {
    let module = &loaded.ctx.module().unwrap();
    let func = match module.functions.iter().find(|f| f.name == test.method_name) {
        Some(f) => f,
        None => return Outcome::Failed {
            reason: format!("test function `{}` not found in merged module", test.method_name),
            location: None,
            stack_trace: None,
        },
    };

    let outcome = interp::run_outcome(&loaded.ctx, module, func, &[]);

    // [ShouldThrow<E>] inverts pass/fail semantics.
    if let Some(expected) = &test.expected_throw {
        return classify_should_throw(outcome, expected, module);
    }

    match outcome {
        Ok(ExecOutcome::Returned(_)) => Outcome::Passed { duration_ms: 0 },
        Ok(ExecOutcome::Thrown(val)) => classify_thrown(&val, module),
        Err(e) => Outcome::Failed {
            reason: format!("VM error: {e}"),
            location: None,
            stack_trace: None,
        },
    }
}

fn classify_should_throw(
    outcome: Result<ExecOutcome>,
    expected: &str,
    module: &Module,
) -> Outcome {
    let candidates: Vec<&str> = expected.split(';').filter(|s| !s.is_empty()).collect();
    let display = candidates.first().copied().unwrap_or(expected);

    match outcome {
        Ok(ExecOutcome::Returned(_)) => Outcome::Failed {
            reason: format!("expected to throw `{display}`, but no exception was thrown"),
            location: None,
            stack_trace: None,
        },
        Err(e) => Outcome::Failed {
            reason: format!("expected to throw `{display}`, got VM error: {e}"),
            location: None,
            stack_trace: None,
        },
        Ok(ExecOutcome::Thrown(val)) => {
            let actual = type_of_value(&val);
            if candidates.iter().any(|c| crate::exec::type_matches(c, &actual)) {
                Outcome::Passed { duration_ms: 0 }
            } else {
                // Surface stack of the *unexpected* throw — helps debug why
                // a different exception type leaked.
                let details = format_failure_with_stack(&val, module);
                Outcome::Failed {
                    reason: format!("expected to throw `{display}`, got `{actual}`"),
                    location: details.primary_location,
                    stack_trace: details.stack_trace,
                }
            }
        }
    }
}

fn classify_thrown(val: &Value, module: &Module) -> Outcome {
    let type_name = type_of_value(val);
    let details = format_failure_with_stack(val, module);
    if type_name.ends_with(".SkipSignal") || type_name == "SkipSignal" {
        // SkipSignal is a control flow signal, not a failure — don't expose
        // its stack (would just be noise: every Assert.Skip throw site is
        // legitimate).
        return Outcome::Skipped { reason: details.message };
    }
    if type_name.ends_with(".TestFailure") || type_name == "TestFailure" {
        return Outcome::Failed {
            reason: details.message,
            location: details.primary_location,
            stack_trace: details.stack_trace,
        };
    }
    Outcome::Failed {
        reason: format!("uncaught exception: {}", details.message),
        location: details.primary_location,
        stack_trace: details.stack_trace,
    }
}

/// FQ class name of an exception Value. `Value::Object(rc)` → `rc.borrow().type_desc.name`.
/// For Value::Str (z42 throws string) returns "Str".
fn type_of_value(val: &Value) -> String {
    match val {
        Value::Object(rc) => rc.borrow().type_desc.name.clone(),
        Value::Str(_)     => "Str".to_string(),
        Value::I64(_)     => "I64".to_string(),
        _ => format!("{:?}", val),
    }
}

/// surface-test-failure-source-location (2026-05-30) — extracted from
/// thrown-value summary into a structured tuple so failure metadata
/// (location, stack) flows separately from the human-readable reason
/// through Outcome → TestResult → formatters.
pub(crate) struct FailureDetails {
    /// Human-readable summary line — `"<Type>: <Message>"` or fallback.
    /// Preserves the pre-spec `format_value` content for backward-compatible
    /// `reason` field consumers.
    pub message: String,
    /// First non-framework `<file>:<line>` from the throw's stack trace.
    /// `None` when the value has no populated StackTrace or the entire
    /// stack is framework code (Std.Test.* / .Assert.).
    pub primary_location: Option<String>,
    /// Full multi-line stack trace as `format_stack_trace` produced it.
    /// Unfiltered — full visibility for deep-debugging framework-internal
    /// bugs.
    pub stack_trace: Option<String>,
}

/// Convert a thrown Value into structured failure metadata. Combines the
/// pre-spec `format_value` (Message extraction) with new stack-trace
/// surfacing via `z42::exception::read_stack_trace`.
pub(crate) fn format_failure_with_stack(val: &Value, module: &Module) -> FailureDetails {
    let message = format_message_only(val);
    let stack_trace = z42::exception::read_stack_trace(val, module);
    let primary_location = stack_trace.as_deref().and_then(first_user_frame);
    FailureDetails { message, primary_location, stack_trace }
}

/// Pre-spec `format_value` logic, preserved verbatim for backward-compatible
/// `reason` output. Reads the `Message` field for Exception subclasses;
/// falls back to a `Type{...}` placeholder or Debug repr.
fn format_message_only(val: &Value) -> String {
    match val {
        Value::Object(rc) => {
            let b = rc.borrow();
            // Try to find a `Message` field for human-readable summary.
            if let Some(&slot) = b.type_desc.field_index.get("Message") {
                if let Some(Value::Str(s)) = b.slots.get(slot) {
                    return format!("{}: {}", b.type_desc.name, s);
                }
            }
            format!("{}{{...}}", b.type_desc.name)
        }
        Value::Str(s) => s.to_string(),
        _ => format!("{:?}", val),
    }
}

/// surface-test-failure-source-location (2026-05-30) — scan a multi-line
/// stack trace (as produced by `z42::exception::format_stack_trace`) and
/// return the first non-framework frame's `<file>:<line>` for IDE jump-to-
/// source.
///
/// Frame line format (producer is z42-internal; we own both ends):
/// `  at <func_name> (<file>:<line>[:<col>])`
///
/// Returns `None` when no parseable user frame is found.
pub(crate) fn first_user_frame(stack: &str) -> Option<String> {
    for raw_line in stack.lines() {
        let line = raw_line.trim_start();
        // Frame must look like "at <func> (<locus>)".
        let after_at = match line.strip_prefix("at ") {
            Some(s) => s,
            None => continue,
        };
        let (func_name, locus) = match after_at.split_once(" (") {
            Some((f, rest)) => (f, rest.trim_end_matches(')')),
            None => continue, // frame has no location → unusable as primary
        };
        if is_framework_frame(func_name) {
            continue;
        }
        // `locus` is one of:
        //   "<file>:<line>"           (zbc 1.0–1.0)
        //   "<file>:<line>:<col>"     (zbc 1.1+ when column > 0)
        //   "line N" / "line N, col M" (no file, fallback shape)
        // Strategy: split off trailing numeric segments and treat the
        // remainder as the file. Drop column if present so primary stays
        // terse and easy to click-through.
        let segments: Vec<&str> = locus.rsplitn(3, ':').collect();
        let location = match segments.as_slice() {
            // "<file>:<line>:<col>" → segments = [col, line, file]
            [col, line, file]
                if col.chars().all(|c| c.is_ascii_digit())
                    && line.chars().all(|c| c.is_ascii_digit()) =>
            {
                format!("{file}:{line}")
            }
            // "<file>:<line>" → segments = [line, file] (rsplitn yields 2)
            [line, file] if line.chars().all(|c| c.is_ascii_digit()) => {
                format!("{file}:{line}")
            }
            // No parseable file:line shape (e.g. "line 42" fallback). Skip
            // — primary-location feature degrades gracefully to None.
            _ => continue,
        };
        return Some(location);
    }
    None
}

/// surface-test-failure-source-location (2026-05-30) — predicate for "this
/// stack frame is z42.test framework code we should hide when extracting
/// the user's primary location".
///
/// Rules (Decision 2 in design.md):
/// - func_name starts with `"Std.Test."` → framework
/// - func_name contains `".Assert."` → framework (user namespaces like
///   `MyApp.Asserts.foo` are intentionally caught when middle-named
///   `Assert`; rare false positives traded for simpler rule)
pub(crate) fn is_framework_frame(func_name: &str) -> bool {
    func_name.starts_with("Std.Test.") || func_name.contains(".Assert.")
}

fn collect_kind_names(test_index: &[TestEntry], user_func_names: &[String], kind: TestEntryKind) -> Vec<String> {
    test_index.iter()
        .filter(|e| e.kind == kind)
        .filter_map(|e| user_func_names.get(e.method_id as usize).cloned())
        .collect()
}

fn run_teardowns(loaded: &mut LoadedRunner) {
    let teardowns = collect_kind_names(&loaded.test_index, &loaded.user_func_names, TestEntryKind::Teardown);
    for name in &teardowns {
        // Teardown errors are logged but don't override the test outcome;
        // for v0.1 we silently swallow (mirror xUnit behavior).
        let _ = exec_named(&loaded.ctx.module().unwrap(), &loaded.ctx, name, "Teardown");
    }
}

#[cfg(test)]
#[path = "runner_tests.rs"]
mod runner_tests;
