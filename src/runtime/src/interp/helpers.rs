/// Value helper functions shared across the interpreter.
/// These accept `&HashMap<u32, Value>` directly (i.e. `&frame.regs`)
/// because `Frame` is private to `mod.rs`.

use crate::types::Value;
use anyhow::{bail, Result};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;

/// Convert a Value to its string representation.
pub fn value_to_str(v: &Value) -> String {
    match v {
        Value::I32(n)  => n.to_string(),
        Value::I64(n)  => n.to_string(),
        Value::F64(f)  => f.to_string(),
        Value::Bool(b) => b.to_string(),
        Value::Str(s)  => s.clone(),
        Value::Null    => "null".to_string(),
        Value::Array(rc) => {
            let inner: Vec<String> = rc.borrow().iter().map(value_to_str).collect();
            format!("[{}]", inner.join(", "))
        }
        other => format!("{:?}", other),
    }
}

/// Collect register values into a Vec.
pub(super) fn collect_args(regs_map: &HashMap<u32, Value>, regs: &[u32]) -> Result<Vec<Value>> {
    regs.iter()
        .map(|r| regs_map.get(r).cloned().ok_or_else(|| anyhow::anyhow!("undefined register %{r}")))
        .collect()
}

/// Extract a string value from a register map.
pub(super) fn str_val(regs_map: &HashMap<u32, Value>, reg: u32) -> Result<String> {
    match regs_map.get(&reg) {
        Some(Value::Str(s)) => Ok(s.clone()),
        Some(other) => bail!("expected str in register %{reg}, got {:?}", other),
        None => bail!("undefined register %{reg}"),
    }
}

/// Integer/float binary operation with automatic widening.
pub(super) fn int_binop(
    regs_map: &HashMap<u32, Value>,
    a: u32,
    b: u32,
    int_op:   impl Fn(i64, i64) -> i64,
    float_op: impl Fn(f64, f64) -> f64,
) -> Result<Value> {
    let va = regs_map.get(&a).ok_or_else(|| anyhow::anyhow!("undefined register %{a}"))?;
    let vb = regs_map.get(&b).ok_or_else(|| anyhow::anyhow!("undefined register %{b}"))?;
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

/// Extract a bool from a register.
pub(super) fn bool_val(regs_map: &HashMap<u32, Value>, reg: u32) -> Result<bool> {
    match regs_map.get(&reg) {
        Some(Value::Bool(b)) => Ok(*b),
        Some(other) => bail!("expected bool in register %{reg}, got {:?}", other),
        None => bail!("undefined register %{reg}"),
    }
}

/// Numeric less-than comparison with automatic widening.
pub(super) fn numeric_lt(regs_map: &HashMap<u32, Value>, a: u32, b: u32) -> Result<bool> {
    let va = regs_map.get(&a).ok_or_else(|| anyhow::anyhow!("undefined register %{a}"))?;
    let vb = regs_map.get(&b).ok_or_else(|| anyhow::anyhow!("undefined register %{b}"))?;
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

/// Unwrap an Array value, returning its Rc.
pub(super) fn expect_array(v: &Value, ctx: &str) -> Result<Rc<RefCell<Vec<Value>>>> {
    match v {
        Value::Array(rc) => Ok(rc.clone()),
        other => bail!("{}: expected array, got {:?}", ctx, other),
    }
}

/// Extract a String argument from the args slice.
pub(super) fn require_str(args: &[Value], idx: usize, ctx: &str) -> Result<String> {
    match args.get(idx) {
        Some(Value::Str(s)) => Ok(s.clone()),
        Some(other) => bail!("{}: arg {} expected string, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

/// Extract a usize argument from the args slice.
pub(super) fn require_usize(args: &[Value], idx: usize, ctx: &str) -> Result<usize> {
    match args.get(idx) {
        Some(v) => to_usize(v, ctx),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}
