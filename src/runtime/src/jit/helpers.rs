// `extern "C"` helper functions called by JIT-compiled code.
// All Value operations are implemented here; the Cranelift-generated native
// code only handles control flow (branches / jumps).
//
// Convention:
//   Functions that can fail return u8: 0=success, 1=exception (PENDING_EXCEPTION).
//   Functions that cannot fail return ().
#![allow(dangerous_implicit_autorefs)]

use crate::corelib::convert::value_to_str;
use crate::metadata::{ObjectData, Value};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;

use super::frame::{FnEntry, JitFrame, JitModuleCtx};

// ── Exception thread-local ────────────────────────────────────────────────────

thread_local! {
    static PENDING_EXCEPTION: RefCell<Option<Value>> = const { RefCell::new(None) };
}

/// Store a user exception value in the thread-local slot.
pub(super) fn set_exception(v: Value) {
    PENDING_EXCEPTION.with(|p| *p.borrow_mut() = Some(v));
}

/// Consume and return the pending exception, leaving the slot empty.
pub(super) fn take_exception() -> Option<Value> {
    PENDING_EXCEPTION.with(|p| p.borrow_mut().take())
}

/// Build an `anyhow::Error` from the current pending exception and consume it.
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

fn static_get(field: &str) -> Value {
    STATIC_FIELDS.with(|sf| sf.borrow().get(field).cloned().unwrap_or(Value::Null))
}

fn static_set_inner(field: &str, val: Value) {
    STATIC_FIELDS.with(|sf| { sf.borrow_mut().insert(field.to_string(), val); });
}

pub(super) fn static_fields_clear() {
    STATIC_FIELDS.with(|sf| sf.borrow_mut().clear());
}

// ── JIT function type alias ───────────────────────────────────────────────────

/// The ABI of every JIT-compiled z42 function.
pub type JitFn = unsafe extern "C" fn(frame: *mut JitFrame, ctx: *const JitModuleCtx) -> u8;

// ── Helper: integer/float binary op ──────────────────────────────────────────

fn int_binop_helper(
    va: &Value,
    vb: &Value,
    int_op:   impl Fn(i64, i64) -> i64,
    float_op: impl Fn(f64, f64) -> f64,
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

fn int_bitop_helper(
    va: &Value,
    vb: &Value,
    op: impl Fn(i64, i64) -> i64,
) -> anyhow::Result<Value> {
    Ok(match (va, vb) {
        (Value::I32(x), Value::I32(y)) => Value::I32(op(*x as i64, *y as i64) as i32),
        (Value::I64(x), Value::I64(y)) => Value::I64(op(*x, *y)),
        (Value::I32(x), Value::I64(y)) => Value::I64(op(*x as i64, *y)),
        (Value::I64(x), Value::I32(y)) => Value::I64(op(*x, *y as i64)),
        (a, b) => anyhow::bail!("bitwise op requires integral operands, got {:?} and {:?}", a, b),
    })
}

fn numeric_lt_helper(va: &Value, vb: &Value) -> anyhow::Result<bool> {
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

// ═════════════════════════════════════════════════════════════════════════════
// Constants
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_const_i32(frame: *mut JitFrame, dst: u32, val: i32) {
    (*frame).regs[dst as usize] = Value::I32(val);
}

#[no_mangle]
pub unsafe extern "C" fn jit_const_i64(frame: *mut JitFrame, dst: u32, val: i64) {
    (*frame).regs[dst as usize] = Value::I64(val);
}

#[no_mangle]
pub unsafe extern "C" fn jit_const_f64(frame: *mut JitFrame, dst: u32, val: f64) {
    (*frame).regs[dst as usize] = Value::F64(val);
}

#[no_mangle]
pub unsafe extern "C" fn jit_const_bool(frame: *mut JitFrame, dst: u32, val: u8) {
    (*frame).regs[dst as usize] = Value::Bool(val != 0);
}

#[no_mangle]
pub unsafe extern "C" fn jit_const_null(frame: *mut JitFrame, dst: u32) {
    (*frame).regs[dst as usize] = Value::Null;
}

/// Load a string constant from the module string pool.
#[no_mangle]
pub unsafe extern "C" fn jit_const_str(
    frame: *mut JitFrame,
    ctx:   *const JitModuleCtx,
    dst:   u32,
    idx:   u32,
) -> u8 {
    let ctx_ref = &*ctx;
    match ctx_ref.string_pool.get(idx as usize) {
        Some(s) => {
            (*frame).regs[dst as usize] = Value::Str(s.clone());
            0
        }
        None => {
            set_exception(Value::Str(format!("string pool index {} out of range", idx)));
            1
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Copy
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_copy(frame: *mut JitFrame, dst: u32, src: u32) {
    let v = (*frame).regs[src as usize].clone();
    (*frame).regs[dst as usize] = v;
}

// ═════════════════════════════════════════════════════════════════════════════
// Arithmetic
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_add(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let fa = &*frame;
    let va = fa.regs[a as usize].clone();
    let vb = fa.regs[b as usize].clone();
    let result = match (&va, &vb) {
        (Value::Str(sa), Value::Str(sb)) => Value::Str(format!("{}{}", sa, sb)),
        (Value::Str(sa), vb) => Value::Str(format!("{}{}", sa, value_to_str(vb))),
        (va, Value::Str(sb)) => Value::Str(format!("{}{}", value_to_str(va), sb)),
        _ => match int_binop_helper(&va, &vb, |x, y| x + y, |x, y| x + y) {
            Ok(r)  => r,
            Err(e) => { set_exception(Value::Str(e.to_string())); return 1; }
        }
    };
    (*frame).regs[dst as usize] = result;
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_sub(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match int_binop_helper(&va, &vb, |x, y| x - y, |x, y| x - y) {
        Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_mul(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match int_binop_helper(&va, &vb, |x, y| x * y, |x, y| x * y) {
        Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_div(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match int_binop_helper(&va, &vb, |x, y| x / y, |x, y| x / y) {
        Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_rem(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match int_binop_helper(&va, &vb, |x, y| x % y, |x, y| x % y) {
        Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Comparison
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_eq(frame: *mut JitFrame, dst: u32, a: u32, b: u32) {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    (*frame).regs[dst as usize] = Value::Bool(va == vb);
}

#[no_mangle]
pub unsafe extern "C" fn jit_ne(frame: *mut JitFrame, dst: u32, a: u32, b: u32) {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    (*frame).regs[dst as usize] = Value::Bool(va != vb);
}

#[no_mangle]
pub unsafe extern "C" fn jit_lt(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match numeric_lt_helper(&va, &vb) {
        Ok(r)  => { (*frame).regs[dst as usize] = Value::Bool(r); 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_le(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    // LE(a, b) = NOT LT(b, a)
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match numeric_lt_helper(&vb, &va) {
        Ok(r)  => { (*frame).regs[dst as usize] = Value::Bool(!r); 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_gt(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    // GT(a, b) = LT(b, a)
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match numeric_lt_helper(&vb, &va) {
        Ok(r)  => { (*frame).regs[dst as usize] = Value::Bool(r); 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_ge(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    // GE(a, b) = NOT LT(a, b)
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match numeric_lt_helper(&va, &vb) {
        Ok(r)  => { (*frame).regs[dst as usize] = Value::Bool(!r); 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Logical
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_and(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    match (&(*frame).regs[a as usize], &(*frame).regs[b as usize]) {
        (Value::Bool(va), Value::Bool(vb)) => {
            (*frame).regs[dst as usize] = Value::Bool(*va && *vb);
            0
        }
        (va, vb) => {
            set_exception(Value::Str(format!("And: expected bool, got {:?} and {:?}", va, vb)));
            1
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_or(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    match (&(*frame).regs[a as usize], &(*frame).regs[b as usize]) {
        (Value::Bool(va), Value::Bool(vb)) => {
            (*frame).regs[dst as usize] = Value::Bool(*va || *vb);
            0
        }
        (va, vb) => {
            set_exception(Value::Str(format!("Or: expected bool, got {:?} and {:?}", va, vb)));
            1
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_not(frame: *mut JitFrame, dst: u32, src: u32) -> u8 {
    match &(*frame).regs[src as usize] {
        Value::Bool(v) => {
            let b = *v;
            (*frame).regs[dst as usize] = Value::Bool(!b);
            0
        }
        other => {
            set_exception(Value::Str(format!("Not: expected bool, got {:?}", other)));
            1
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Unary arithmetic
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_neg(frame: *mut JitFrame, dst: u32, src: u32) -> u8 {
    let result = match &(*frame).regs[src as usize] {
        Value::I32(n) => Value::I32(-n),
        Value::I64(n) => Value::I64(-n),
        Value::F64(f) => Value::F64(-f),
        other => {
            set_exception(Value::Str(format!("Neg: expected numeric, got {:?}", other)));
            return 1;
        }
    };
    (*frame).regs[dst as usize] = result;
    0
}

// ═════════════════════════════════════════════════════════════════════════════
// Bitwise
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_bit_and(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match int_bitop_helper(&va, &vb, |x, y| x & y) {
        Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_bit_or(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match int_bitop_helper(&va, &vb, |x, y| x | y) {
        Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_bit_xor(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match int_bitop_helper(&va, &vb, |x, y| x ^ y) {
        Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_bit_not(frame: *mut JitFrame, dst: u32, src: u32) -> u8 {
    let result = match &(*frame).regs[src as usize] {
        Value::I32(n) => Value::I32(!n),
        Value::I64(n) => Value::I64(!n),
        other => {
            set_exception(Value::Str(format!("BitNot: expected integral, got {:?}", other)));
            return 1;
        }
    };
    (*frame).regs[dst as usize] = result;
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_shl(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match int_bitop_helper(&va, &vb, |x, y| x << (y & 63)) {
        Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_shr(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match int_bitop_helper(&va, &vb, |x, y| x >> (y & 63)) {
        Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Variable slots
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_store(
    frame:       *mut JitFrame,
    var_ptr:     *const u8,
    var_len:     usize,
    src:         u32,
) {
    let name = std::str::from_utf8(std::slice::from_raw_parts(var_ptr, var_len))
        .unwrap_or("<invalid>");
    let val = (*frame).regs[src as usize].clone();
    (*frame).vars.insert(name.to_string(), val);
}

#[no_mangle]
pub unsafe extern "C" fn jit_load(
    frame:       *mut JitFrame,
    dst:         u32,
    var_ptr:     *const u8,
    var_len:     usize,
) -> u8 {
    let name = std::str::from_utf8(std::slice::from_raw_parts(var_ptr, var_len))
        .unwrap_or("<invalid>");
    match (*frame).vars.get(name).cloned() {
        Some(v) => { (*frame).regs[dst as usize] = v; 0 }
        None    => {
            set_exception(Value::Str(format!("undefined variable `{}`", name)));
            1
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// String
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_str_concat(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    match (&(*frame).regs[a as usize], &(*frame).regs[b as usize]) {
        (Value::Str(sa), Value::Str(sb)) => {
            let r = format!("{}{}", sa, sb);
            (*frame).regs[dst as usize] = Value::Str(r);
            0
        }
        (va, vb) => {
            set_exception(Value::Str(format!("StrConcat: expected two strings, got {:?} and {:?}", va, vb)));
            1
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_to_str(frame: *mut JitFrame, dst: u32, src: u32) {
    let s = value_to_str(&(*frame).regs[src as usize]);
    (*frame).regs[dst as usize] = Value::Str(s);
}

// ═════════════════════════════════════════════════════════════════════════════
// Calls
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_call(
    frame:        *mut JitFrame,
    ctx:          *const JitModuleCtx,
    dst:          u32,
    fn_name_ptr:  *const u8,
    fn_name_len:  usize,
    args_ptr:     *const u32,
    argc:         usize,
) -> u8 {
    let func_name = std::str::from_utf8(std::slice::from_raw_parts(fn_name_ptr, fn_name_len))
        .unwrap_or("<invalid>");
    let ctx_ref   = &*ctx;
    let frame_ref = &mut *frame;

    let entry: &FnEntry = match ctx_ref.fn_entries.get(func_name) {
        Some(e) => e,
        None => {
            set_exception(Value::Str(format!("undefined function `{}`", func_name)));
            return 1;
        }
    };

    let arg_regs = std::slice::from_raw_parts(args_ptr, argc);
    let args: Vec<Value> = arg_regs.iter()
        .map(|&r| frame_ref.regs[r as usize].clone())
        .collect();

    let mut callee_frame = JitFrame::new(entry.max_reg, &args);
    let jit_fn: JitFn = std::mem::transmute(entry.ptr);
    let result = jit_fn(&mut callee_frame, ctx);

    if result != 0 { return 1; }
    frame_ref.regs[dst as usize] = callee_frame.ret.unwrap_or(Value::Null);
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_builtin(
    frame:     *mut JitFrame,
    dst:       u32,
    name_ptr:  *const u8,
    name_len:  usize,
    args_ptr:  *const u32,
    argc:      usize,
) -> u8 {
    let name = std::str::from_utf8(std::slice::from_raw_parts(name_ptr, name_len))
        .unwrap_or("<invalid>");
    let frame_ref = &mut *frame;
    let arg_regs  = std::slice::from_raw_parts(args_ptr, argc);
    let args: Vec<Value> = arg_regs.iter()
        .map(|&r| frame_ref.regs[r as usize].clone())
        .collect();

    match crate::corelib::exec_builtin(name, &args) {
        Ok(v)  => { frame_ref.regs[dst as usize] = v; 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Arrays
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_array_new(frame: *mut JitFrame, dst: u32, size: u32) -> u8 {
    let n = match &(*frame).regs[size as usize] {
        Value::I32(n) if *n >= 0 => *n as usize,
        Value::I64(n) if *n >= 0 => *n as usize,
        other => {
            set_exception(Value::Str(format!("ArrayNew: expected non-negative int, got {:?}", other)));
            return 1;
        }
    };
    let arr = Rc::new(RefCell::new(vec![Value::Null; n]));
    (*frame).regs[dst as usize] = Value::Array(arr);
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_new_lit(
    frame:     *mut JitFrame,
    dst:       u32,
    elems_ptr: *const u32,
    elem_cnt:  usize,
) {
    let elems = std::slice::from_raw_parts(elems_ptr, elem_cnt);
    let vals: Vec<Value> = elems.iter().map(|&r| (*frame).regs[r as usize].clone()).collect();
    (*frame).regs[dst as usize] = Value::Array(Rc::new(RefCell::new(vals)));
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_get(frame: *mut JitFrame, dst: u32, arr: u32, idx: u32) -> u8 {
    let arr_val = (*frame).regs[arr as usize].clone();
    let idx_val = (*frame).regs[idx as usize].clone();
    let result = match &arr_val {
        Value::Array(rc) => {
            let i = match &idx_val {
                Value::I32(n) if *n >= 0 => *n as usize,
                Value::I64(n) if *n >= 0 => *n as usize,
                other => {
                    set_exception(Value::Str(format!("ArrayGet: bad index {:?}", other)));
                    return 1;
                }
            };
            let borrowed = rc.borrow();
            if i >= borrowed.len() {
                set_exception(Value::Str(format!("array index {} out of bounds (len={})", i, borrowed.len())));
                return 1;
            }
            borrowed[i].clone()
        }
        Value::Map(rc) => {
            let key = value_to_str(&idx_val);
            rc.borrow().get(&key).cloned().unwrap_or(Value::Null)
        }
        other => {
            set_exception(Value::Str(format!("ArrayGet: expected array or map, got {:?}", other)));
            return 1;
        }
    };
    (*frame).regs[dst as usize] = result;
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_set(frame: *mut JitFrame, arr: u32, idx: u32, val: u32) -> u8 {
    let arr_val = (*frame).regs[arr as usize].clone();
    let idx_val = (*frame).regs[idx as usize].clone();
    let v       = (*frame).regs[val as usize].clone();
    match &arr_val {
        Value::Array(rc) => {
            let i = match &idx_val {
                Value::I32(n) if *n >= 0 => *n as usize,
                Value::I64(n) if *n >= 0 => *n as usize,
                other => {
                    set_exception(Value::Str(format!("ArraySet: bad index {:?}", other)));
                    return 1;
                }
            };
            let mut borrowed = rc.borrow_mut();
            if i >= borrowed.len() {
                set_exception(Value::Str(format!("array index {} out of bounds (len={})", i, borrowed.len())));
                return 1;
            }
            borrowed[i] = v;
        }
        Value::Map(rc) => {
            let key = value_to_str(&idx_val);
            rc.borrow_mut().insert(key, v);
        }
        other => {
            set_exception(Value::Str(format!("ArraySet: expected array or map, got {:?}", other)));
            return 1;
        }
    }
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_len(frame: *mut JitFrame, dst: u32, arr: u32) -> u8 {
    match &(*frame).regs[arr as usize] {
        Value::Array(rc) => {
            let len = rc.borrow().len() as i32;
            (*frame).regs[dst as usize] = Value::I32(len);
            0
        }
        other => {
            set_exception(Value::Str(format!("ArrayLen: expected array, got {:?}", other)));
            1
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Objects
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_obj_new(
    frame:          *mut JitFrame,
    ctx:            *const JitModuleCtx,
    dst:            u32,
    cls_name_ptr:   *const u8,
    cls_name_len:   usize,
    args_ptr:       *const u32,
    argc:           usize,
) -> u8 {
    let class_name = std::str::from_utf8(std::slice::from_raw_parts(cls_name_ptr, cls_name_len))
        .unwrap_or("<invalid>")
        .to_string();
    let ctx_ref   = &*ctx;
    let module    = &*ctx_ref.module;
    let frame_ref = &mut *frame;

    // Build fields by walking inheritance chain
    let mut chain: Vec<&crate::metadata::ClassDesc> = Vec::new();
    let mut cur = class_name.as_str();
    loop {
        if let Some(desc) = module.classes.iter().find(|c| c.name == cur) {
            chain.push(desc);
            match &desc.base_class {
                Some(b) => cur = b.as_str(),
                None    => break,
            }
        } else {
            break;
        }
    }
    let mut fields: HashMap<String, Value> = HashMap::new();
    for desc in chain.iter().rev() {
        for f in &desc.fields {
            fields.entry(f.name.clone()).or_insert(Value::Null);
        }
    }

    let obj_rc  = Rc::new(RefCell::new(ObjectData { class_name: class_name.clone(), fields }));
    let obj_val = Value::Object(obj_rc);

    // Collect constructor args
    let arg_regs = std::slice::from_raw_parts(args_ptr, argc);
    let mut ctor_args: Vec<Value> = vec![obj_val.clone()];
    ctor_args.extend(arg_regs.iter().map(|&r| frame_ref.regs[r as usize].clone()));

    // Call constructor if present
    let simple_name = class_name.split('.').last().unwrap_or(class_name.as_str());
    let ctor_name   = format!("{}.{}", class_name, simple_name);

    if let Some(entry) = ctx_ref.fn_entries.get(&ctor_name) {
        let mut callee = JitFrame::new(entry.max_reg, &ctor_args);
        let jit_fn: JitFn = std::mem::transmute(entry.ptr);
        let r = jit_fn(&mut callee, ctx);
        if r != 0 { return 1; }
    }

    frame_ref.regs[dst as usize] = obj_val;
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_field_get(
    frame:          *mut JitFrame,
    dst:            u32,
    obj:            u32,
    field_name_ptr: *const u8,
    field_name_len: usize,
) -> u8 {
    let field_name = std::str::from_utf8(std::slice::from_raw_parts(field_name_ptr, field_name_len))
        .unwrap_or("<invalid>");
    let val = match &(*frame).regs[obj as usize] {
        Value::Object(rc) => rc.borrow().fields.get(field_name).cloned().unwrap_or(Value::Null),
        other => {
            set_exception(Value::Str(format!("FieldGet: expected object, got {:?}", other)));
            return 1;
        }
    };
    (*frame).regs[dst as usize] = val;
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_field_set(
    frame:          *mut JitFrame,
    obj:            u32,
    field_name_ptr: *const u8,
    field_name_len: usize,
    val:            u32,
) -> u8 {
    let field_name = std::str::from_utf8(std::slice::from_raw_parts(field_name_ptr, field_name_len))
        .unwrap_or("<invalid>")
        .to_string();
    let v = (*frame).regs[val as usize].clone();
    match &(*frame).regs[obj as usize] {
        Value::Object(rc) => {
            rc.borrow_mut().fields.insert(field_name, v);
            0
        }
        other => {
            set_exception(Value::Str(format!("FieldSet: expected object, got {:?}", other)));
            1
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_vcall(
    frame:      *mut JitFrame,
    ctx:        *const JitModuleCtx,
    dst:        u32,
    obj:        u32,
    method_ptr: *const u8,
    method_len: usize,
    args_ptr:   *const u32,
    argc:       usize,
) -> u8 {
    let method    = std::str::from_utf8(std::slice::from_raw_parts(method_ptr, method_len))
        .unwrap_or("<invalid>");
    let ctx_ref   = &*ctx;
    let module    = &*ctx_ref.module;
    let frame_ref = &mut *frame;

    let class_name = match &frame_ref.regs[obj as usize] {
        Value::Object(rc) => rc.borrow().class_name.clone(),
        other => {
            set_exception(Value::Str(format!("VCall: expected object, got {:?}", other)));
            return 1;
        }
    };

    // Walk inheritance chain to find method
    let func_name = match resolve_virtual(module, &class_name, method) {
        Ok(n)  => n,
        Err(e) => { set_exception(Value::Str(e.to_string())); return 1; }
    };

    let entry = match ctx_ref.fn_entries.get(&func_name) {
        Some(e) => e,
        None => {
            set_exception(Value::Str(format!("VCall: compiled entry for `{}` not found", func_name)));
            return 1;
        }
    };

    let arg_regs = std::slice::from_raw_parts(args_ptr, argc);
    let obj_val  = frame_ref.regs[obj as usize].clone();
    let mut call_args: Vec<Value> = vec![obj_val];
    call_args.extend(arg_regs.iter().map(|&r| frame_ref.regs[r as usize].clone()));

    let mut callee = JitFrame::new(entry.max_reg, &call_args);
    let jit_fn: JitFn = std::mem::transmute(entry.ptr);
    let r = jit_fn(&mut callee, ctx);
    if r != 0 { return 1; }
    frame_ref.regs[dst as usize] = callee.ret.unwrap_or(Value::Null);
    0
}

/// Walk the class hierarchy to find the fully-qualified method name.
fn resolve_virtual(module: &crate::metadata::Module, class_name: &str, method: &str) -> anyhow::Result<String> {
    let mut cur = class_name;
    loop {
        let qualified = format!("{}.{}", cur, method);
        if module.functions.iter().any(|f| f.name == qualified) {
            return Ok(qualified);
        }
        match module.classes.iter().find(|c| c.name == cur).and_then(|c| c.base_class.as_deref()) {
            Some(base) => cur = base,
            None => anyhow::bail!("VCall: no implementation of `{}` found in hierarchy of `{}`", method, class_name),
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// IsInstance / AsCast
// ═════════════════════════════════════════════════════════════════════════════

fn is_subclass_or_eq(module: &crate::metadata::Module, derived: &str, target: &str) -> bool {
    let mut cur = derived;
    loop {
        if cur == target { return true; }
        match module.classes.iter().find(|c| c.name == cur).and_then(|c| c.base_class.as_deref()) {
            Some(base) => cur = base,
            None       => return false,
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_is_instance(
    frame:     *mut JitFrame,
    ctx:       *const JitModuleCtx,
    dst:       u32,
    obj:       u32,
    cls_ptr:   *const u8,
    cls_len:   usize,
) {
    let class_name = std::str::from_utf8(std::slice::from_raw_parts(cls_ptr, cls_len))
        .unwrap_or("<invalid>");
    let module = &*(*ctx).module;
    let result = match &(*frame).regs[obj as usize] {
        Value::Object(rc) => {
            let runtime = rc.borrow().class_name.clone();
            is_subclass_or_eq(module, &runtime, class_name)
        }
        _ => false,
    };
    (*frame).regs[dst as usize] = Value::Bool(result);
}

#[no_mangle]
pub unsafe extern "C" fn jit_as_cast(
    frame:     *mut JitFrame,
    ctx:       *const JitModuleCtx,
    dst:       u32,
    obj:       u32,
    cls_ptr:   *const u8,
    cls_len:   usize,
) {
    let class_name = std::str::from_utf8(std::slice::from_raw_parts(cls_ptr, cls_len))
        .unwrap_or("<invalid>");
    let module = &*(*ctx).module;
    let val    = (*frame).regs[obj as usize].clone();
    let is_match = match &val {
        Value::Object(rc) => {
            let runtime = rc.borrow().class_name.clone();
            is_subclass_or_eq(module, &runtime, class_name)
        }
        Value::Null => true,
        _           => false,
    };
    (*frame).regs[dst as usize] = if is_match { val } else { Value::Null };
}

// ═════════════════════════════════════════════════════════════════════════════
// Static fields
// ═════════════════════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn jit_static_get(
    frame:      *mut JitFrame,
    dst:        u32,
    field_ptr:  *const u8,
    field_len:  usize,
) {
    let field = std::str::from_utf8(std::slice::from_raw_parts(field_ptr, field_len))
        .unwrap_or("<invalid>");
    (*frame).regs[dst as usize] = static_get(field);
}

#[no_mangle]
pub unsafe extern "C" fn jit_static_set(
    frame:      *mut JitFrame,
    field_ptr:  *const u8,
    field_len:  usize,
    val:        u32,
) {
    let field = std::str::from_utf8(std::slice::from_raw_parts(field_ptr, field_len))
        .unwrap_or("<invalid>")
        .to_string();
    let v = (*frame).regs[val as usize].clone();
    static_set_inner(&field, v);
}

// ═════════════════════════════════════════════════════════════════════════════
// Control-flow helpers
// ═════════════════════════════════════════════════════════════════════════════

/// Extract the bool value from a register; returns 1 if true, 0 if false.
/// Sets a pending exception and returns 255 on type error.
#[no_mangle]
pub unsafe extern "C" fn jit_get_bool(frame: *mut JitFrame, reg: u32) -> u8 {
    match &(*frame).regs[reg as usize] {
        Value::Bool(b) => if *b { 1 } else { 0 },
        other => {
            set_exception(Value::Str(format!("BrCond: expected bool, got {:?}", other)));
            255
        }
    }
}

/// Copy frame.regs[reg] into frame.ret (the return value slot).
#[no_mangle]
pub unsafe extern "C" fn jit_set_ret(frame: *mut JitFrame, reg: u32) {
    let v = (*frame).regs[reg as usize].clone();
    (*frame).ret = Some(v);
}

/// Store frame.regs[reg] as the pending exception.
#[no_mangle]
pub unsafe extern "C" fn jit_throw(frame: *mut JitFrame, reg: u32) {
    let v = (*frame).regs[reg as usize].clone();
    set_exception(v);
}

/// Read the pending exception into frame.regs[catch_reg] (used after catching).
#[no_mangle]
pub unsafe extern "C" fn jit_install_catch(frame: *mut JitFrame, catch_reg: u32) {
    let v = take_exception().unwrap_or(Value::Null);
    (*frame).regs[catch_reg as usize] = v;
}
