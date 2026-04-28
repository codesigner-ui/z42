#![allow(dangerous_implicit_autorefs)]
// JIT helpers — arithmetic, comparison, logical, unary, bitwise operations.

use crate::metadata::Value;
use super::frame::{JitFrame, JitModuleCtx};
use super::helpers::{set_exception, vm_ctx_ref, int_binop_helper, int_bitop_helper, numeric_lt_helper};
use crate::corelib::convert::value_to_str;

// ── Arithmetic ───────────────────────────────────────────────────────────────

// 2026-04-28 vm-wrapping-int-arith: Add/Sub/Mul 用 wrapping，与 interp 对齐 +
// C# unchecked / Java int / Rust release default 一致。Div/Rem 不变（panic
// on /0 是不同语义）。

#[no_mangle]
pub unsafe extern "C" fn jit_add(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, a: u32, b: u32,
) -> u8 {
    // Fast path: both I64
    let regs = &(*frame).regs;
    if let (Value::I64(x), Value::I64(y)) = (&regs[a as usize], &regs[b as usize]) {
        (*frame).regs[dst as usize] = Value::I64(x.wrapping_add(*y));
        return 0;
    }
    let va = regs[a as usize].clone();
    let vb = regs[b as usize].clone();
    let result = match (&va, &vb) {
        (Value::Str(sa), Value::Str(sb)) => Value::Str(format!("{}{}", sa, sb)),
        (Value::Str(sa), vb) => Value::Str(format!("{}{}", sa, value_to_str(vb))),
        (va, Value::Str(sb)) => Value::Str(format!("{}{}", value_to_str(va), sb)),
        _ => match int_binop_helper(&va, &vb, i64::wrapping_add, |x, y| x + y) {
            Ok(r)  => r,
            Err(e) => { set_exception(vm_ctx_ref(ctx), Value::Str(e.to_string())); return 1; }
        }
    };
    (*frame).regs[dst as usize] = result;
    0
}

macro_rules! arith_op {
    ($name:ident, $int_op:expr, $float_op:expr) => {
        #[no_mangle]
        pub unsafe extern "C" fn $name(
            frame: *mut JitFrame, ctx: *const JitModuleCtx,
            dst: u32, a: u32, b: u32,
        ) -> u8 {
            // Fast path: both I64 — no clone, no match dispatch
            let regs = &(*frame).regs;
            if let (Value::I64(x), Value::I64(y)) = (&regs[a as usize], &regs[b as usize]) {
                let int_op: fn(i64, i64) -> i64 = $int_op;
                (*frame).regs[dst as usize] = Value::I64(int_op(*x, *y));
                return 0;
            }
            let va = regs[a as usize].clone();
            let vb = regs[b as usize].clone();
            match int_binop_helper(&va, &vb, $int_op, $float_op) {
                Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
                Err(e) => { set_exception(vm_ctx_ref(ctx), Value::Str(e.to_string())); 1 }
            }
        }
    };
}

arith_op!(jit_sub, i64::wrapping_sub, |x, y| x - y);
arith_op!(jit_mul, i64::wrapping_mul, |x, y| x * y);
arith_op!(jit_div, |x, y| x / y, |x, y| x / y);
arith_op!(jit_rem, |x, y| x % y, |x, y| x % y);

// ── Comparison ───────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_eq(
    frame: *mut JitFrame, _ctx: *const JitModuleCtx,
    dst: u32, a: u32, b: u32,
) {
    let regs = &(*frame).regs;
    // Fast path: both I64
    if let (Value::I64(x), Value::I64(y)) = (&regs[a as usize], &regs[b as usize]) {
        (*frame).regs[dst as usize] = Value::Bool(x == y);
        return;
    }
    let va = regs[a as usize].clone();
    let vb = regs[b as usize].clone();
    (*frame).regs[dst as usize] = Value::Bool(va == vb);
}

#[no_mangle]
pub unsafe extern "C" fn jit_ne(
    frame: *mut JitFrame, _ctx: *const JitModuleCtx,
    dst: u32, a: u32, b: u32,
) {
    let regs = &(*frame).regs;
    // Fast path: both I64
    if let (Value::I64(x), Value::I64(y)) = (&regs[a as usize], &regs[b as usize]) {
        (*frame).regs[dst as usize] = Value::Bool(x != y);
        return;
    }
    let va = regs[a as usize].clone();
    let vb = regs[b as usize].clone();
    (*frame).regs[dst as usize] = Value::Bool(va != vb);
}

macro_rules! cmp_op {
    ($name:ident, $i64_op:expr, $lt_swap:expr, $negate:expr) => {
        #[no_mangle]
        pub unsafe extern "C" fn $name(
            frame: *mut JitFrame, ctx: *const JitModuleCtx,
            dst: u32, a: u32, b: u32,
        ) -> u8 {
            let regs = &(*frame).regs;
            // Fast path: both I64
            if let (Value::I64(x), Value::I64(y)) = (&regs[a as usize], &regs[b as usize]) {
                let cmp: fn(&i64, &i64) -> bool = $i64_op;
                (*frame).regs[dst as usize] = Value::Bool(cmp(x, y));
                return 0;
            }
            let (va, vb) = if $lt_swap {
                (regs[b as usize].clone(), regs[a as usize].clone())
            } else {
                (regs[a as usize].clone(), regs[b as usize].clone())
            };
            match numeric_lt_helper(&va, &vb) {
                Ok(r)  => { (*frame).regs[dst as usize] = Value::Bool(if $negate { !r } else { r }); 0 }
                Err(e) => { set_exception(vm_ctx_ref(ctx), Value::Str(e.to_string())); 1 }
            }
        }
    };
}

cmp_op!(jit_lt, |x: &i64, y: &i64| x < y,  false, false);
cmp_op!(jit_le, |x: &i64, y: &i64| x <= y, true,  true);
cmp_op!(jit_gt, |x: &i64, y: &i64| x > y,  true,  false);
cmp_op!(jit_ge, |x: &i64, y: &i64| x >= y, false, true);

// ── Logical ──────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_and(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, a: u32, b: u32,
) -> u8 {
    match (&(*frame).regs[a as usize], &(*frame).regs[b as usize]) {
        (Value::Bool(va), Value::Bool(vb)) => { (*frame).regs[dst as usize] = Value::Bool(*va && *vb); 0 }
        (va, vb) => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("And: expected bool, got {:?} and {:?}", va, vb)));
            1
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_or(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, a: u32, b: u32,
) -> u8 {
    match (&(*frame).regs[a as usize], &(*frame).regs[b as usize]) {
        (Value::Bool(va), Value::Bool(vb)) => { (*frame).regs[dst as usize] = Value::Bool(*va || *vb); 0 }
        (va, vb) => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("Or: expected bool, got {:?} and {:?}", va, vb)));
            1
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_not(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, src: u32,
) -> u8 {
    match &(*frame).regs[src as usize] {
        Value::Bool(v) => { let b = *v; (*frame).regs[dst as usize] = Value::Bool(!b); 0 }
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("Not: expected bool, got {:?}", other)));
            1
        }
    }
}

// ── Unary arithmetic ─────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_neg(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, src: u32,
) -> u8 {
    let result = match &(*frame).regs[src as usize] {
        Value::I64(n) => Value::I64(-n),
        Value::F64(f) => Value::F64(-f),
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("Neg: expected numeric, got {:?}", other)));
            return 1;
        }
    };
    (*frame).regs[dst as usize] = result;
    0
}

// ── Bitwise ──────────────────────────────────────────────────────────────────

macro_rules! bitwise_op {
    ($name:ident, $op:expr) => {
        #[no_mangle]
        pub unsafe extern "C" fn $name(
            frame: *mut JitFrame, ctx: *const JitModuleCtx,
            dst: u32, a: u32, b: u32,
        ) -> u8 {
            let va = (*frame).regs[a as usize].clone();
            let vb = (*frame).regs[b as usize].clone();
            match int_bitop_helper(&va, &vb, $op) {
                Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
                Err(e) => { set_exception(vm_ctx_ref(ctx), Value::Str(e.to_string())); 1 }
            }
        }
    };
}

bitwise_op!(jit_bit_and, |x, y| x & y);
bitwise_op!(jit_bit_or,  |x, y| x | y);
bitwise_op!(jit_bit_xor, |x, y| x ^ y);
bitwise_op!(jit_shl,     |x, y| x << (y & 63));
bitwise_op!(jit_shr,     |x, y| x >> (y & 63));

#[no_mangle]
pub unsafe extern "C" fn jit_bit_not(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, src: u32,
) -> u8 {
    let result = match &(*frame).regs[src as usize] {
        Value::I64(n) => Value::I64(!n),
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("BitNot: expected integral, got {:?}", other)));
            return 1;
        }
    };
    (*frame).regs[dst as usize] = result;
    0
}
