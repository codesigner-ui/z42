/// `extern "C"` helper functions called by JIT-compiled code.
///
/// All Value operations are implemented here; the Cranelift-generated native
/// code only handles control flow (branches / jumps).
///
/// Convention:
///   Functions that can fail return u8: 0=success, 1=exception (stored on ctx).
///   Functions that cannot fail return ().
///
/// Split into submodules by category:
///   helpers.rs        — shared state access (exceptions, static fields), common helpers
///   helpers_arith.rs  — arithmetic, comparison, logical, unary, bitwise
///   helpers_mem.rs    — constants, copy, variable slots, string ops, control-flow
///   helpers_object.rs — function calls, arrays, objects, type checks, static fields

use crate::corelib::convert::value_to_str;
use crate::metadata::Value;
use crate::vm_context::VmContext;

use super::frame::{JitFrame, JitModuleCtx};

// ── VmContext access via JitModuleCtx ───────────────────────────────────────
//
// Every JIT helper now receives `*const JitModuleCtx` as its 2nd parameter
// (after `*mut JitFrame`). The `JitModuleCtx::vm_ctx: *mut VmContext` field
// (set by `JitModule::run` for the duration of one entry call) is the only
// runtime-mutable VM state — the previous `PENDING_EXCEPTION` and
// `STATIC_FIELDS` thread_local slots have been removed.
//
// extend-jit-helper-abi (2026-04-28) — closes the C1 follow-up that left
// these as `sync_in_from_ctx` / `sync_out_to_ctx` bridges.

/// Borrow the VmContext from a JitModuleCtx pointer for the duration of the
/// helper call.
///
/// SAFETY: caller must ensure
///   1. `jit_ctx` is non-null and points to a valid JitModuleCtx
///   2. `(*jit_ctx).vm_ctx` is non-null (always true while inside
///      `JitModule::run` / `run_fn`)
///   3. The returned reference's lifetime does not outlive the helper call
pub(super) unsafe fn vm_ctx_ref<'a>(jit_ctx: *const JitModuleCtx) -> &'a VmContext {
    &*((*jit_ctx).vm_ctx)
}

pub(super) fn set_exception(ctx: &VmContext, v: Value) {
    ctx.set_exception(v);
}

pub(super) fn take_exception(ctx: &VmContext) -> Option<Value> {
    ctx.take_exception()
}

pub(super) fn take_exception_error(ctx: &VmContext) -> anyhow::Error {
    let msg = take_exception(ctx)
        .as_ref()
        .map(value_to_str)
        .unwrap_or_else(|| "uncaught exception".to_owned());
    anyhow::anyhow!("{}", msg)
}

// `static_get` / `static_set` are accessed directly through `VmContext` methods
// at the helper call sites (see `helpers_object::jit_static_get/set`); no
// helper-layer wrapper needed.

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
