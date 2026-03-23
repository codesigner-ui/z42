/// Value helper functions shared across the interpreter.
/// These accept `&HashMap<u32, Value>` directly (i.e. `&frame.regs`)
/// because `Frame` is private to `mod.rs`.

use crate::types::Value;
use anyhow::{bail, Result};
use std::collections::HashMap;

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
        Value::Object(rc) => format!("{}{{...}}", rc.borrow().class_name),
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

/// Integer-only binary operation (bitwise/shift).  Rejects floats.
pub(super) fn int_bitop(
    regs_map: &HashMap<u32, Value>,
    a: u32,
    b: u32,
    op: impl Fn(i64, i64) -> i64,
) -> Result<Value> {
    let va = regs_map.get(&a).ok_or_else(|| anyhow::anyhow!("undefined register %{a}"))?;
    let vb = regs_map.get(&b).ok_or_else(|| anyhow::anyhow!("undefined register %{b}"))?;
    Ok(match (va, vb) {
        (Value::I32(x), Value::I32(y)) => Value::I32(op(*x as i64, *y as i64) as i32),
        (Value::I64(x), Value::I64(y)) => Value::I64(op(*x, *y)),
        (Value::I32(x), Value::I64(y)) => Value::I64(op(*x as i64, *y)),
        (Value::I64(x), Value::I32(y)) => Value::I64(op(*x, *y as i64)),
        (a, b) => bail!("bitwise op requires integral operands, got {:?} and {:?}", a, b),
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

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    fn regs(pairs: &[(u32, Value)]) -> HashMap<u32, Value> {
        pairs.iter().cloned().collect()
    }

    // ── value_to_str ─────────────────────────────────────────────────────────

    #[test]
    fn value_to_str_i32() {
        assert_eq!(value_to_str(&Value::I32(42)), "42");
    }

    #[test]
    fn value_to_str_i64() {
        assert_eq!(value_to_str(&Value::I64(-1)), "-1");
    }

    #[test]
    fn value_to_str_bool() {
        assert_eq!(value_to_str(&Value::Bool(true)), "true");
        assert_eq!(value_to_str(&Value::Bool(false)), "false");
    }

    #[test]
    fn value_to_str_null() {
        assert_eq!(value_to_str(&Value::Null), "null");
    }

    #[test]
    fn value_to_str_str() {
        assert_eq!(value_to_str(&Value::Str("hi".into())), "hi");
    }

    #[test]
    fn value_to_str_array() {
        let arr = Value::Array(Rc::new(RefCell::new(vec![
            Value::I32(1), Value::I32(2), Value::I32(3),
        ])));
        assert_eq!(value_to_str(&arr), "[1, 2, 3]");
    }

    #[test]
    fn value_to_str_empty_array() {
        let arr = Value::Array(Rc::new(RefCell::new(vec![])));
        assert_eq!(value_to_str(&arr), "[]");
    }

    // ── int_binop ─────────────────────────────────────────────────────────────

    #[test]
    fn int_binop_i32_add() {
        let m = regs(&[(0, Value::I32(3)), (1, Value::I32(4))]);
        assert_eq!(int_binop(&m, 0, 1, |a, b| a + b, |a, b| a + b).unwrap(), Value::I32(7));
    }

    #[test]
    fn int_binop_i64_add() {
        let m = regs(&[(0, Value::I64(10)), (1, Value::I64(20))]);
        assert_eq!(int_binop(&m, 0, 1, |a, b| a + b, |a, b| a + b).unwrap(), Value::I64(30));
    }

    #[test]
    fn int_binop_widen_i32_i64() {
        let m = regs(&[(0, Value::I32(5)), (1, Value::I64(10))]);
        assert_eq!(int_binop(&m, 0, 1, |a, b| a + b, |a, b| a + b).unwrap(), Value::I64(15));
    }

    #[test]
    fn int_binop_f64_mul() {
        let m = regs(&[(0, Value::F64(2.0)), (1, Value::F64(3.0))]);
        assert_eq!(int_binop(&m, 0, 1, |a, b| a * b, |a, b| a * b).unwrap(), Value::F64(6.0));
    }

    #[test]
    fn int_binop_type_mismatch_errors() {
        let m = regs(&[(0, Value::Str("a".into())), (1, Value::I32(1))]);
        assert!(int_binop(&m, 0, 1, |a, b| a + b, |a, b| a + b).is_err());
    }

    // ── numeric_lt ────────────────────────────────────────────────────────────

    #[test]
    fn numeric_lt_i32_true() {
        let m = regs(&[(0, Value::I32(1)), (1, Value::I32(2))]);
        assert!(numeric_lt(&m, 0, 1).unwrap());
    }

    #[test]
    fn numeric_lt_i32_false() {
        let m = regs(&[(0, Value::I32(5)), (1, Value::I32(3))]);
        assert!(!numeric_lt(&m, 0, 1).unwrap());
    }

    #[test]
    fn numeric_lt_equal_is_false() {
        let m = regs(&[(0, Value::I32(3)), (1, Value::I32(3))]);
        assert!(!numeric_lt(&m, 0, 1).unwrap());
    }

    #[test]
    fn numeric_lt_i32_i64_widening() {
        let m = regs(&[(0, Value::I32(1)), (1, Value::I64(1000))]);
        assert!(numeric_lt(&m, 0, 1).unwrap());
    }

    // ── to_usize ──────────────────────────────────────────────────────────────

    #[test]
    fn to_usize_from_i32() {
        assert_eq!(to_usize(&Value::I32(5), "test").unwrap(), 5);
    }

    #[test]
    fn to_usize_from_i64() {
        assert_eq!(to_usize(&Value::I64(100), "test").unwrap(), 100);
    }

    #[test]
    fn to_usize_negative_errors() {
        assert!(to_usize(&Value::I32(-1), "test").is_err());
    }

    #[test]
    fn to_usize_wrong_type_errors() {
        assert!(to_usize(&Value::Str("x".into()), "test").is_err());
    }

    // ── require_str ───────────────────────────────────────────────────────────

    #[test]
    fn require_str_ok() {
        let args = vec![Value::Str("hello".into())];
        assert_eq!(require_str(&args, 0, "test").unwrap(), "hello");
    }

    #[test]
    fn require_str_missing_errors() {
        assert!(require_str(&[], 0, "test").is_err());
    }

    #[test]
    fn require_str_wrong_type_errors() {
        let args = vec![Value::I32(1)];
        assert!(require_str(&args, 0, "test").is_err());
    }
}
