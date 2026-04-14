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
use std::cell::RefCell;
use std::collections::HashMap;

use super::frame::{JitFrame, JitModuleCtx};

// ── Exception thread-local ────────────────────────────────────────────────────

thread_local! {
    static PENDING_EXCEPTION: RefCell<Option<Value>> = const { RefCell::new(None) };
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

// ── Static fields thread-local ────────────────────────────────────────────────

thread_local! {
    static STATIC_FIELDS: RefCell<HashMap<String, Value>> = RefCell::new(HashMap::new());
}

pub(super) fn static_get(field: &str) -> Value {
    STATIC_FIELDS.with(|sf| sf.borrow().get(field).cloned().unwrap_or(Value::Null))
}

pub(super) fn static_set_inner(field: &str, val: Value) {
    STATIC_FIELDS.with(|sf| { sf.borrow_mut().insert(field.to_string(), val); });
}

pub(super) fn static_fields_clear() {
    STATIC_FIELDS.with(|sf| sf.borrow_mut().clear());
}

// ── JIT function type alias ───────────────────────────────────────────────────

pub type JitFn = unsafe extern "C" fn(frame: *mut JitFrame, ctx: *const JitModuleCtx) -> u8;

// ── Shared numeric helpers ────────────────────────────────────────────────────

pub(super) fn int_binop_helper(
    va: &Value, vb: &Value,
    int_op: impl Fn(i64, i64) -> i64, float_op: impl Fn(f64, f64) -> f64,
) -> anyhow::Result<Value> {
    Ok(match (va, vb) {
        (Value::I32(x), Value::I32(y)) => Value::I32(int_op(*x as i64, *y as i64) as i32),
        (Value::I64(x), Value::I64(y)) => Value::I64(int_op(*x, *y)),
        (Value::I32(x), Value::I64(y)) => Value::I64(int_op(*x as i64, *y)),
        (Value::I64(x), Value::I32(y)) => Value::I64(int_op(*x, *y as i64)),
        (Value::F64(x), Value::F64(y)) => Value::F64(float_op(*x, *y)),
        (Value::F64(x), Value::I64(y)) => Value::F64(float_op(*x, *y as f64)),
        (Value::I64(x), Value::F64(y)) => Value::F64(float_op(*x as f64, *y)),
        (Value::F64(x), Value::I32(y)) => Value::F64(float_op(*x, *y as f64)),
        (Value::I32(x), Value::F64(y)) => Value::F64(float_op(*x as f64, *y)),
        (a, b) => anyhow::bail!("type mismatch in arithmetic: {:?} vs {:?}", a, b),
    })
}

pub(super) fn int_bitop_helper(
    va: &Value, vb: &Value, op: impl Fn(i64, i64) -> i64,
) -> anyhow::Result<Value> {
    Ok(match (va, vb) {
        (Value::I32(x), Value::I32(y)) => Value::I32(op(*x as i64, *y as i64) as i32),
        (Value::I64(x), Value::I64(y)) => Value::I64(op(*x, *y)),
        (Value::I32(x), Value::I64(y)) => Value::I64(op(*x as i64, *y)),
        (Value::I64(x), Value::I32(y)) => Value::I64(op(*x, *y as i64)),
        (a, b) => anyhow::bail!("bitwise op requires integral operands, got {:?} and {:?}", a, b),
    })
}

pub(super) fn numeric_lt_helper(va: &Value, vb: &Value) -> anyhow::Result<bool> {
    Ok(match (va, vb) {
        (Value::I32(x), Value::I32(y)) => x < y,
        (Value::I64(x), Value::I64(y)) => x < y,
        (Value::I32(x), Value::I64(y)) => (*x as i64) < *y,
        (Value::I64(x), Value::I32(y)) => *x < (*y as i64),
        (Value::F64(x), Value::F64(y)) => x < y,
        (Value::F64(x), Value::I64(y)) => *x < (*y as f64),
        (Value::I64(x), Value::F64(y)) => (*x as f64) < *y,
        (Value::F64(x), Value::I32(y)) => *x < (*y as f64),
        (Value::I32(x), Value::F64(y)) => (*x as f64) < *y,
        (a, b) => anyhow::bail!("type mismatch in comparison: {:?} vs {:?}", a, b),
    })
}
