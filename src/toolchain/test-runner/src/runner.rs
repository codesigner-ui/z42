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

pub fn run_one(loaded: &mut LoadedRunner, test: &DiscoveredTest<'_>) -> Outcome {
    if let Some(reason) = &test.skip_reason {
        return Outcome::Skipped { reason: reason.clone() };
    }

    let start = Instant::now();

    // 1. Per-test isolation: clear static fields + re-run all __static_init__.
    if let Err(e) = interp::init_static_fields(&loaded.ctx, &loaded.vm.module) {
        return Outcome::Failed {
            reason: format!("static init error: {e}"),
        };
    }

    // 2. Setup methods (sequential; first failure short-circuits to test fail).
    let setup_names = collect_kind_names(&loaded.test_index, &loaded.user_func_names, TestEntryKind::Setup);
    for setup in &setup_names {
        if let Some(Outcome::Failed { reason }) = exec_named(&loaded.vm.module, &loaded.ctx, setup, "Setup") {
            run_teardowns(loaded);
            return Outcome::Failed { reason };
        }
    }

    // 3. Test body.
    let outcome = exec_test_body(loaded, test);

    // 4. Teardown methods (always, even on test fail — mirrors xUnit).
    run_teardowns(loaded);

    // 5. Wallclock includes Setup/Teardown time (TODO: split if needed for
    //    accurate Bencher reporting).
    let duration_ms = start.elapsed().as_millis() as u64;

    match outcome {
        Outcome::Passed { .. } => Outcome::Passed { duration_ms },
        other => other, // Failed / Skipped already carries reason
    }
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
        Ok(ExecOutcome::Thrown(val)) => Outcome::Failed {
            reason: format!("{role}: uncaught exception: {}", format_value(&val)),
        },
        Err(e) => Outcome::Failed {
            reason: format!("{role}: VM error: {e}"),
        },
    }
}

fn exec_test_body(loaded: &LoadedRunner, test: &DiscoveredTest<'_>) -> Outcome {
    let module = &loaded.vm.module;
    let func = match module.functions.iter().find(|f| f.name == test.method_name) {
        Some(f) => f,
        None => return Outcome::Failed {
            reason: format!("test function `{}` not found in merged module", test.method_name),
        },
    };

    let outcome = interp::run_outcome(&loaded.ctx, module, func, &[]);

    // [ShouldThrow<E>] inverts pass/fail semantics.
    if let Some(expected) = &test.expected_throw {
        return classify_should_throw(outcome, expected);
    }

    match outcome {
        Ok(ExecOutcome::Returned(_)) => Outcome::Passed { duration_ms: 0 },
        Ok(ExecOutcome::Thrown(val)) => classify_thrown(&val),
        Err(e) => Outcome::Failed { reason: format!("VM error: {e}") },
    }
}

fn classify_should_throw(outcome: Result<ExecOutcome>, expected: &str) -> Outcome {
    let candidates: Vec<&str> = expected.split(';').filter(|s| !s.is_empty()).collect();
    let display = candidates.first().copied().unwrap_or(expected);

    match outcome {
        Ok(ExecOutcome::Returned(_)) => Outcome::Failed {
            reason: format!("expected to throw `{display}`, but no exception was thrown"),
        },
        Err(e) => Outcome::Failed {
            reason: format!("expected to throw `{display}`, got VM error: {e}"),
        },
        Ok(ExecOutcome::Thrown(val)) => {
            let actual = type_of_value(&val);
            if candidates.iter().any(|c| crate::exec::type_matches(c, &actual)) {
                Outcome::Passed { duration_ms: 0 }
            } else {
                Outcome::Failed {
                    reason: format!("expected to throw `{display}`, got `{actual}`"),
                }
            }
        }
    }
}

fn classify_thrown(val: &Value) -> Outcome {
    let type_name = type_of_value(val);
    let formatted = format_value(val);
    if type_name.ends_with(".SkipSignal") || type_name == "SkipSignal" {
        return Outcome::Skipped { reason: formatted };
    }
    if type_name.ends_with(".TestFailure") || type_name == "TestFailure" {
        return Outcome::Failed { reason: formatted };
    }
    Outcome::Failed { reason: format!("uncaught exception: {formatted}") }
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

/// Format an exception value for the failure reason. Uses Object's type +
/// best-effort field summary; falls back to Debug.
fn format_value(val: &Value) -> String {
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
        Value::Str(s) => s.clone(),
        _ => format!("{:?}", val),
    }
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
        let _ = exec_named(&loaded.vm.module, &loaded.ctx, name, "Teardown");
    }
}
