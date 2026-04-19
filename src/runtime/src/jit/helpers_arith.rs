#![allow(dangerous_implicit_autorefs)]
// JIT helpers — arithmetic, comparison, logical, unary, bitwise operations.

use crate::metadata::Value;
use super::frame::JitFrame;
use super::helpers::{set_exception, int_binop_helper, int_bitop_helper, numeric_lt_helper};
use crate::corelib::convert::value_to_str;

// ── Arithmetic ───────────────────────────────────────────────────────────────

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

macro_rules! arith_op {
    ($name:ident, $int_op:expr, $float_op:expr) => {
        #[no_mangle]
        pub unsafe extern "C" fn $name(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
            let va = (*frame).regs[a as usize].clone();
            let vb = (*frame).regs[b as usize].clone();
            match int_binop_helper(&va, &vb, $int_op, $float_op) {
                Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
                Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
            }
        }
    };
}

arith_op!(jit_sub, |x, y| x - y, |x, y| x - y);
arith_op!(jit_mul, |x, y| x * y, |x, y| x * y);
arith_op!(jit_div, |x, y| x / y, |x, y| x / y);
arith_op!(jit_rem, |x, y| x % y, |x, y| x % y);

// ── Comparison ───────────────────────────────────────────────────────────────

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
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match numeric_lt_helper(&vb, &va) {
        Ok(r)  => { (*frame).regs[dst as usize] = Value::Bool(!r); 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_gt(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match numeric_lt_helper(&vb, &va) {
        Ok(r)  => { (*frame).regs[dst as usize] = Value::Bool(r); 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_ge(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    let va = (*frame).regs[a as usize].clone();
    let vb = (*frame).regs[b as usize].clone();
    match numeric_lt_helper(&va, &vb) {
        Ok(r)  => { (*frame).regs[dst as usize] = Value::Bool(!r); 0 }
        Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
    }
}

// ── Logical ──────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_and(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    match (&(*frame).regs[a as usize], &(*frame).regs[b as usize]) {
        (Value::Bool(va), Value::Bool(vb)) => { (*frame).regs[dst as usize] = Value::Bool(*va && *vb); 0 }
        (va, vb) => { set_exception(Value::Str(format!("And: expected bool, got {:?} and {:?}", va, vb))); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_or(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    match (&(*frame).regs[a as usize], &(*frame).regs[b as usize]) {
        (Value::Bool(va), Value::Bool(vb)) => { (*frame).regs[dst as usize] = Value::Bool(*va || *vb); 0 }
        (va, vb) => { set_exception(Value::Str(format!("Or: expected bool, got {:?} and {:?}", va, vb))); 1 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_not(frame: *mut JitFrame, dst: u32, src: u32) -> u8 {
    match &(*frame).regs[src as usize] {
        Value::Bool(v) => { let b = *v; (*frame).regs[dst as usize] = Value::Bool(!b); 0 }
        other => { set_exception(Value::Str(format!("Not: expected bool, got {:?}", other))); 1 }
    }
}

// ── Unary arithmetic ─────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_neg(frame: *mut JitFrame, dst: u32, src: u32) -> u8 {
    let result = match &(*frame).regs[src as usize] {
        Value::I64(n) => Value::I64(-n),
        Value::F64(f) => Value::F64(-f),
        other => { set_exception(Value::Str(format!("Neg: expected numeric, got {:?}", other))); return 1; }
    };
    (*frame).regs[dst as usize] = result;
    0
}

// ── Bitwise ──────────────────────────────────────────────────────────────────

macro_rules! bitwise_op {
    ($name:ident, $op:expr) => {
        #[no_mangle]
        pub unsafe extern "C" fn $name(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
            let va = (*frame).regs[a as usize].clone();
            let vb = (*frame).regs[b as usize].clone();
            match int_bitop_helper(&va, &vb, $op) {
                Ok(r)  => { (*frame).regs[dst as usize] = r; 0 }
                Err(e) => { set_exception(Value::Str(e.to_string())); 1 }
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
pub unsafe extern "C" fn jit_bit_not(frame: *mut JitFrame, dst: u32, src: u32) -> u8 {
    let result = match &(*frame).regs[src as usize] {
        Value::I64(n) => Value::I64(!n),
        other => { set_exception(Value::Str(format!("BitNot: expected integral, got {:?}", other))); return 1; }
    };
    (*frame).regs[dst as usize] = result;
    0
}
