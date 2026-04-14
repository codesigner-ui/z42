/// Register-level helper operations for the interpreter execution loop.
///
/// These functions operate directly on the register slice (`&[Value]`)
/// and are specific to the interpreter's execution model.
/// Value conversion helpers (value_to_str, require_str, etc.) live in corelib::convert.

use crate::metadata::Value;
use anyhow::{bail, Result};

/// Collect register values into a Vec.
pub(super) fn collect_args(regs: &[Value], reg_indices: &[u32]) -> Result<Vec<Value>> {
    reg_indices.iter()
        .map(|&r| regs.get(r as usize).cloned().ok_or_else(|| anyhow::anyhow!("undefined register %{r}")))
        .collect()
}

/// Extract a string value from a register slice.
pub(super) fn str_val(regs: &[Value], reg: u32) -> Result<String> {
    match regs.get(reg as usize) {
        Some(Value::Str(s)) => Ok(s.clone()),
        Some(other) => bail!("expected str in register %{reg}, got {:?}", other),
        None => bail!("undefined register %{reg}"),
    }
}

/// Extract a bool from a register.
pub(super) fn bool_val(regs: &[Value], reg: u32) -> Result<bool> {
    match regs.get(reg as usize) {
        Some(Value::Bool(b)) => Ok(*b),
        Some(other) => bail!("expected bool in register %{reg}, got {:?}", other),
        None => bail!("undefined register %{reg}"),
    }
}

/// Integer/float binary operation with automatic widening.
pub(super) fn int_binop(
    regs: &[Value],
    a: u32,
    b: u32,
    int_op:   impl Fn(i64, i64) -> i64,
    float_op: impl Fn(f64, f64) -> f64,
) -> Result<Value> {
    let va = regs.get(a as usize).ok_or_else(|| anyhow::anyhow!("undefined register %{a}"))?;
    let vb = regs.get(b as usize).ok_or_else(|| anyhow::anyhow!("undefined register %{b}"))?;
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
        (a, b) => bail!("type mismatch in arithmetic: {:?} vs {:?}", a, b),
    })
}

/// Integer-only binary operation (bitwise/shift). Rejects floats.
pub(super) fn int_bitop(
    regs: &[Value],
    a: u32,
    b: u32,
    op: impl Fn(i64, i64) -> i64,
) -> Result<Value> {
    let va = regs.get(a as usize).ok_or_else(|| anyhow::anyhow!("undefined register %{a}"))?;
    let vb = regs.get(b as usize).ok_or_else(|| anyhow::anyhow!("undefined register %{b}"))?;
    Ok(match (va, vb) {
        (Value::I32(x), Value::I32(y)) => Value::I32(op(*x as i64, *y as i64) as i32),
        (Value::I64(x), Value::I64(y)) => Value::I64(op(*x, *y)),
        (Value::I32(x), Value::I64(y)) => Value::I64(op(*x as i64, *y)),
        (Value::I64(x), Value::I32(y)) => Value::I64(op(*x, *y as i64)),
        (a, b) => bail!("bitwise op requires integral operands, got {:?} and {:?}", a, b),
    })
}

/// Numeric less-than comparison with automatic widening.
pub(super) fn numeric_lt(regs: &[Value], a: u32, b: u32) -> Result<bool> {
    let va = regs.get(a as usize).ok_or_else(|| anyhow::anyhow!("undefined register %{a}"))?;
    let vb = regs.get(b as usize).ok_or_else(|| anyhow::anyhow!("undefined register %{b}"))?;
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
        (a, b) => bail!("type mismatch in comparison: {:?} vs {:?}", a, b),
    })
}

/// Convert a Value to a usize index/size, rejecting negative values.
pub(super) fn to_usize(v: &Value, ctx: &str) -> Result<usize> {
    match v {
        Value::I32(n) if *n >= 0 => Ok(*n as usize),
        Value::I64(n) if *n >= 0 => Ok(*n as usize),
        other => bail!("{}: expected non-negative integer, got {:?}", ctx, other),
    }
}
