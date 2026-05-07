/// Value-shuffling instructions: constants, copy, arithmetic, comparison,
/// logical, unary, bitwise, string formation. Pure register operations —
/// none of these can throw user exceptions; all errors are VM-internal.

use crate::metadata::{Module, Value};
use crate::vm_context::VmContext;
use anyhow::{bail, Result};

use super::dispatch::{obj_to_string, value_to_str};
use super::ops::{bool_val, int_binop, int_bitop, numeric_lt, str_val};
use super::Frame;

// ── Constants ────────────────────────────────────────────────────────────

pub(super) fn const_str(
    ctx: &VmContext, module: &Module, frame: &mut Frame, dst: u32, idx: u32,
) -> Result<()> {
    let i = idx as usize;
    let s = if let Some(s) = module.string_pool.get(i) {
        s.clone()
    } else if let Some(s) = ctx.try_lookup_string(i) {
        // ConstStr from a lazily-loaded function — idx is offset past main pool.
        s
    } else {
        bail!("string pool index {idx} out of range");
    };
    frame.set(dst, Value::Str(s));
    Ok(())
}

pub(super) fn const_i32(frame: &mut Frame, dst: u32, val: i32)   { frame.set(dst, Value::I64(val as i64)); }
pub(super) fn const_i64(frame: &mut Frame, dst: u32, val: i64)   { frame.set(dst, Value::I64(val)); }
pub(super) fn const_f64(frame: &mut Frame, dst: u32, val: f64)   { frame.set(dst, Value::F64(val)); }
pub(super) fn const_bool(frame: &mut Frame, dst: u32, val: bool) { frame.set(dst, Value::Bool(val)); }
pub(super) fn const_char(frame: &mut Frame, dst: u32, val: char) { frame.set(dst, Value::Char(val)); }
pub(super) fn const_null(frame: &mut Frame, dst: u32)            { frame.set(dst, Value::Null); }

pub(super) fn copy(frame: &mut Frame, dst: u32, src: u32) -> Result<()> {
    let v = frame.get(src)?.clone();
    frame.set(dst, v);
    Ok(())
}

// ── Arithmetic ───────────────────────────────────────────────────────────

pub(super) fn add(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    let result = match (frame.get(a)?, frame.get(b)?) {
        (Value::Str(sa), Value::Str(sb)) => Value::Str(format!("{}{}", sa, sb)),
        (Value::Str(sa), vb)             => Value::Str(format!("{}{}", sa, value_to_str(vb))),
        (va, Value::Str(sb))             => Value::Str(format!("{}{}", value_to_str(va), sb)),
        // 2026-04-28 vm-wrapping-int-arith: wrapping_add（与 Rust release build /
        // C# unchecked int / Java int 一致），解锁 hash / PRNG / 校验和算法
        _ => int_binop(&frame.regs, a, b, i64::wrapping_add, |x, y| x + y)?,
    };
    frame.set(dst, result);
    Ok(())
}

pub(super) fn sub(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, int_binop(&frame.regs, a, b, i64::wrapping_sub, |x, y| x - y)?);
    Ok(())
}

pub(super) fn mul(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, int_binop(&frame.regs, a, b, i64::wrapping_mul, |x, y| x * y)?);
    Ok(())
}

pub(super) fn div(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, int_binop(&frame.regs, a, b, |x, y| x / y, |x, y| x / y)?);
    Ok(())
}

pub(super) fn rem(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, int_binop(&frame.regs, a, b, |x, y| x % y, |x, y| x % y)?);
    Ok(())
}

// ── Comparison ───────────────────────────────────────────────────────────

pub(super) fn eq(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, Value::Bool(frame.get(a)? == frame.get(b)?));
    Ok(())
}

pub(super) fn ne(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, Value::Bool(frame.get(a)? != frame.get(b)?));
    Ok(())
}

pub(super) fn lt(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, Value::Bool(numeric_lt(&frame.regs, a, b)?));
    Ok(())
}

pub(super) fn le(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, Value::Bool(!numeric_lt(&frame.regs, b, a)?));
    Ok(())
}

pub(super) fn gt(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, Value::Bool(numeric_lt(&frame.regs, b, a)?));
    Ok(())
}

pub(super) fn ge(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, Value::Bool(!numeric_lt(&frame.regs, a, b)?));
    Ok(())
}

// ── Logical ──────────────────────────────────────────────────────────────

pub(super) fn and(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, Value::Bool(bool_val(&frame.regs, a)? && bool_val(&frame.regs, b)?));
    Ok(())
}

pub(super) fn or(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, Value::Bool(bool_val(&frame.regs, a)? || bool_val(&frame.regs, b)?));
    Ok(())
}

pub(super) fn not(frame: &mut Frame, dst: u32, src: u32) -> Result<()> {
    frame.set(dst, Value::Bool(!bool_val(&frame.regs, src)?));
    Ok(())
}

// ── Unary arithmetic ─────────────────────────────────────────────────────

pub(super) fn neg(frame: &mut Frame, dst: u32, src: u32) -> Result<()> {
    let res = match frame.get(src)? {
        Value::I64(n) => Value::I64(-n),
        Value::F64(f) => Value::F64(-f),
        other => bail!("Neg: expected numeric, got {:?}", other),
    };
    frame.set(dst, res);
    Ok(())
}

// ── Bitwise ──────────────────────────────────────────────────────────────

pub(super) fn bit_and(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, int_bitop(&frame.regs, a, b, |x, y| x & y)?);
    Ok(())
}

pub(super) fn bit_or(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, int_bitop(&frame.regs, a, b, |x, y| x | y)?);
    Ok(())
}

pub(super) fn bit_xor(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, int_bitop(&frame.regs, a, b, |x, y| x ^ y)?);
    Ok(())
}

pub(super) fn bit_not(frame: &mut Frame, dst: u32, src: u32) -> Result<()> {
    let res = match frame.get(src)? {
        Value::I64(n) => Value::I64(!n),
        other => bail!("BitNot: expected integral, got {:?}", other),
    };
    frame.set(dst, res);
    Ok(())
}

pub(super) fn shl(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, int_bitop(&frame.regs, a, b, |x, y| x << (y & 63))?);
    Ok(())
}

pub(super) fn shr(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    frame.set(dst, int_bitop(&frame.regs, a, b, |x, y| x >> (y & 63))?);
    Ok(())
}

// ── String formation ─────────────────────────────────────────────────────

pub(super) fn str_concat(frame: &mut Frame, dst: u32, a: u32, b: u32) -> Result<()> {
    let sa = str_val(&frame.regs, a)?;
    let sb = str_val(&frame.regs, b)?;
    frame.set(dst, Value::Str(format!("{}{}", sa, sb)));
    Ok(())
}

pub(super) fn to_str(
    ctx: &VmContext, module: &Module, frame: &mut Frame, dst: u32, src: u32,
) -> Result<()> {
    let s = obj_to_string(ctx, module, frame.get(src)?)?;
    frame.set(dst, Value::Str(s));
    Ok(())
}
