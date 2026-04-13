use crate::metadata::Value;
use anyhow::{bail, Result};

/// Convert a Value to its string representation.
pub fn value_to_str(v: &Value) -> String {
    match v {
        Value::I32(n)  => n.to_string(),
        Value::I64(n)  => n.to_string(),
        Value::F64(f)  => f.to_string(),
        Value::Bool(b) => b.to_string(),
        Value::Char(c) => c.to_string(),
        Value::Str(s)  => s.clone(),
        Value::Null    => "null".to_string(),
        Value::Array(rc) => {
            let inner: Vec<String> = rc.borrow().iter().map(value_to_str).collect();
            format!("[{}]", inner.join(", "))
        }
        Value::Object(rc) => format!("{}{{...}}", rc.borrow().type_desc.name),
        other => format!("{:?}", other),
    }
}

/// Extract a String argument from the args slice.
pub fn require_str(args: &[Value], idx: usize, ctx: &str) -> Result<String> {
    match args.get(idx) {
        Some(Value::Str(s)) => Ok(s.clone()),
        Some(other) => bail!("{}: arg {} expected string, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

/// Extract a usize argument from the args slice.
pub fn require_usize(args: &[Value], idx: usize, ctx: &str) -> Result<usize> {
    match args.get(idx) {
        Some(v) => to_usize(v, ctx),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

/// Convert a Value to a usize index/size, rejecting negative values.
pub fn to_usize(v: &Value, ctx: &str) -> Result<usize> {
    match v {
        Value::I32(n) if *n >= 0 => Ok(*n as usize),
        Value::I64(n) if *n >= 0 => Ok(*n as usize),
        other => bail!("{}: expected non-negative integer, got {:?}", ctx, other),
    }
}

// ── Parse / convert builtins ─────────────────────────────────────────────────

pub fn builtin_long_parse(args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "long.Parse")?;
    s.trim().parse::<i64>().map(Value::I64)
        .map_err(|_| anyhow::anyhow!("long.Parse: could not parse {:?} as long", s))
}
pub fn builtin_int_parse(args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "int.Parse")?;
    s.trim().parse::<i64>().map(Value::I64)
        .map_err(|_| anyhow::anyhow!("int.Parse: could not parse {:?} as int", s))
}
pub fn builtin_double_parse(args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "double.Parse")?;
    s.trim().parse::<f64>().map(Value::F64)
        .map_err(|_| anyhow::anyhow!("double.Parse: could not parse {:?} as double", s))
}
pub fn builtin_to_str(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(args.first().map(value_to_str).unwrap_or_default()))
}
