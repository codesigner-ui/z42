/// `extern "C"` helper functions called by JIT-compiled code.
///
/// All Value operations are implemented here; the Cranelift-generated native
/// code only handles control flow (branches / jumps).
///
/// Convention:
///   Functions that can fail return u8: 0=success, 1=exception (PENDING_EXCEPTION).
///   Functions that cannot fail return ().
///
/// Split into submodules by category:
///   helpers.rs        — shared state (exceptions, static fields), common helpers
///   helpers_arith.rs  — arithmetic, comparison, logical, unary, bitwise
///   helpers_mem.rs    — constants, copy, variable slots, string ops, control-flow
///   helpers_object.rs — function calls, arrays, objects, type checks, static fields

use crate::corelib::convert::value_to_str;
use crate::metadata::Value;
use crate::vm_context::VmContext;
use std::cell::RefCell;
use std::collections::HashMap;

use super::frame::{JitFrame, JitModuleCtx};

// ── JIT-internal exception + static-fields slots ──────────────────────────────
//
// These two `thread_local!` slots remain as JIT extern "C" helper backing
// store; the broader VmContext is the canonical owner (consolidate-vm-state,
// 2026-04-28 — Decision 5). `JitModule::run` syncs them with `ctx.*` at
// the run boundary (sync-in before entry, sync-out after return), so:
//
// - Each `VmContext` sees its own static-field map and exception slot
//   across separate `run` calls (no cross-ctx pollution).
// - Mid-run JIT helpers still operate on thread_local (no ABI change).
// - Concurrent JIT execution on the same thread for two different ctxes
//   would corrupt these slots — but `JitModule::run` is synchronous, so
//   that's structurally impossible without a future async-JIT redesign.
//
// Removing these requires extending every helper signature (~30 fns) with
// `ctx: *const JitModuleCtx` and updating Cranelift call sites. Tracked as
// follow-up "extend-jit-helper-abi" spec.

thread_local! {
    static PENDING_EXCEPTION: RefCell<Option<Value>> = const { RefCell::new(None) };
    static STATIC_FIELDS:     RefCell<HashMap<String, Value>> = RefCell::new(HashMap::new());
}

pub(super) fn set_exception(v: Value) {
    PENDING_EXCEPTION.with(|p| *p.borrow_mut() = Some(v));
}

pub(super) fn take_exception() -> Option<Value> {
    PENDING_EXCEPTION.with(|p| p.borrow_mut().take())
}

pub(super) fn take_exception_error() -> anyhow::Error {
    let msg = take_exception()
        .as_ref()
        .map(value_to_str)
        .unwrap_or_else(|| "uncaught exception".to_owned());
    anyhow::anyhow!("{}", msg)
}

pub(super) fn static_get(field: &str) -> Value {
    STATIC_FIELDS.with(|sf| sf.borrow().get(field).cloned().unwrap_or(Value::Null))
}

pub(super) fn static_set_inner(field: &str, val: Value) {
    STATIC_FIELDS.with(|sf| { sf.borrow_mut().insert(field.to_string(), val); });
}

#[allow(dead_code)]
pub(super) fn static_fields_clear() {
    STATIC_FIELDS.with(|sf| sf.borrow_mut().clear());
}

// ── VmContext sync helpers (called by JitModule::run) ────────────────────────

/// Snapshot ctx state into the JIT thread_local slots before entering a
/// JIT-compiled function. Called by `JitModule::run` at the boundary.
pub(super) fn sync_in_from_ctx(ctx: &VmContext) {
    STATIC_FIELDS.with(|sf| {
        let mut sf = sf.borrow_mut();
        sf.clear();
        for (k, v) in ctx.static_fields.borrow().iter() {
            sf.insert(k.clone(), v.clone());
        }
    });
    PENDING_EXCEPTION.with(|p| *p.borrow_mut() = ctx.pending_exception.borrow_mut().take());
}

/// Write JIT thread_local state back into ctx after a JIT-compiled function
/// returns. Called by `JitModule::run` at the boundary.
pub(super) fn sync_out_to_ctx(ctx: &VmContext) {
    let snapshot: HashMap<String, Value> =
        STATIC_FIELDS.with(|sf| sf.borrow().clone());
    *ctx.static_fields.borrow_mut() = snapshot;
    let pending = PENDING_EXCEPTION.with(|p| p.borrow_mut().take());
    *ctx.pending_exception.borrow_mut() = pending;
}

// ── JIT function type alias ───────────────────────────────────────────────────

pub type JitFn = unsafe extern "C" fn(frame: *mut JitFrame, ctx: *const JitModuleCtx) -> u8;

// ── Shared numeric helpers ────────────────────────────────────────────────────

pub(super) fn int_binop_helper(
    va: &Value, vb: &Value,
    int_op: impl Fn(i64, i64) -> i64, float_op: impl Fn(f64, f64) -> f64,
) -> anyhow::Result<Value> {
    Ok(match (va, vb) {
        (Value::I64(x), Value::I64(y)) => Value::I64(int_op(*x, *y)),
        (Value::F64(x), Value::F64(y)) => Value::F64(float_op(*x, *y)),
        (Value::F64(x), Value::I64(y)) => Value::F64(float_op(*x, *y as f64)),
        (Value::I64(x), Value::F64(y)) => Value::F64(float_op(*x as f64, *y)),
        (a, b) => anyhow::bail!("type mismatch in arithmetic: {:?} vs {:?}", a, b),
    })
}

pub(super) fn int_bitop_helper(
    va: &Value, vb: &Value, op: impl Fn(i64, i64) -> i64,
) -> anyhow::Result<Value> {
    Ok(match (va, vb) {
        (Value::I64(x), Value::I64(y)) => Value::I64(op(*x, *y)),
        (a, b) => anyhow::bail!("bitwise op requires integral operands, got {:?} and {:?}", a, b),
    })
}

pub(super) fn numeric_lt_helper(va: &Value, vb: &Value) -> anyhow::Result<bool> {
    Ok(match (va, vb) {
        (Value::I64(x), Value::I64(y)) => x < y,
        (Value::F64(x), Value::F64(y)) => x < y,
        (Value::F64(x), Value::I64(y)) => *x < (*y as f64),
        (Value::I64(x), Value::F64(y)) => (*x as f64) < *y,
        (a, b) => anyhow::bail!("type mismatch in comparison: {:?} vs {:?}", a, b),
    })
}
