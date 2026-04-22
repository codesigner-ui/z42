use crate::metadata::Value;
use anyhow::{bail, Result};

/// Convert a Value to its string representation.
pub fn value_to_str(v: &Value) -> String {
    match v {
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
        Value::I64(n) if *n >= 0 => Ok(*n as usize),
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

// ── L3-G4b primitive interface implementations ──────────────────────────────
// Backing native functions for IComparable<T> / IEquatable<T> on primitive
// receivers (int/double/bool/char). Dispatched by VCall when the receiver
// is Value::I64/F64/Bool/Char and the method matches CompareTo/Equals/GetHashCode.

fn require_i64(args: &[Value], idx: usize, ctx: &str) -> Result<i64> {
    match args.get(idx) {
        Some(Value::I64(n)) => Ok(*n),
        Some(other) => bail!("{}: arg {} expected int, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}
fn require_f64(args: &[Value], idx: usize, ctx: &str) -> Result<f64> {
    match args.get(idx) {
        Some(Value::F64(f)) => Ok(*f),
        Some(Value::I64(n)) => Ok(*n as f64),
        Some(other) => bail!("{}: arg {} expected double, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}
fn require_bool(args: &[Value], idx: usize, ctx: &str) -> Result<bool> {
    match args.get(idx) {
        Some(Value::Bool(b)) => Ok(*b),
        Some(other) => bail!("{}: arg {} expected bool, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}
fn require_char(args: &[Value], idx: usize, ctx: &str) -> Result<char> {
    match args.get(idx) {
        Some(Value::Char(c)) => Ok(*c),
        Some(other) => bail!("{}: arg {} expected char, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

pub fn builtin_int_compare_to(args: &[Value]) -> Result<Value> {
    let a = require_i64(args, 0, "int.CompareTo")?;
    let b = require_i64(args, 1, "int.CompareTo")?;
    Ok(Value::I64(a.cmp(&b) as i64))
}
pub fn builtin_int_equals(args: &[Value]) -> Result<Value> {
    let a = require_i64(args, 0, "int.Equals")?;
    let b = require_i64(args, 1, "int.Equals")?;
    Ok(Value::Bool(a == b))
}
pub fn builtin_int_hash_code(args: &[Value]) -> Result<Value> {
    let a = require_i64(args, 0, "int.GetHashCode")?;
    Ok(Value::I64(a))  // identity hash for integers
}
pub fn builtin_int_to_string(args: &[Value]) -> Result<Value> {
    let a = require_i64(args, 0, "int.ToString")?;
    Ok(Value::Str(a.to_string()))
}

pub fn builtin_double_compare_to(args: &[Value]) -> Result<Value> {
    let a = require_f64(args, 0, "double.CompareTo")?;
    let b = require_f64(args, 1, "double.CompareTo")?;
    Ok(Value::I64(a.partial_cmp(&b).map(|o| o as i64).unwrap_or(0)))
}
pub fn builtin_double_equals(args: &[Value]) -> Result<Value> {
    let a = require_f64(args, 0, "double.Equals")?;
    let b = require_f64(args, 1, "double.Equals")?;
    Ok(Value::Bool(a == b))
}
pub fn builtin_double_hash_code(args: &[Value]) -> Result<Value> {
    let a = require_f64(args, 0, "double.GetHashCode")?;
    Ok(Value::I64(a.to_bits() as i64))
}
pub fn builtin_double_to_string(args: &[Value]) -> Result<Value> {
    let a = require_f64(args, 0, "double.ToString")?;
    Ok(Value::Str(a.to_string()))
}

pub fn builtin_bool_equals(args: &[Value]) -> Result<Value> {
    let a = require_bool(args, 0, "bool.Equals")?;
    let b = require_bool(args, 1, "bool.Equals")?;
    Ok(Value::Bool(a == b))
}
pub fn builtin_bool_hash_code(args: &[Value]) -> Result<Value> {
    let a = require_bool(args, 0, "bool.GetHashCode")?;
    Ok(Value::I64(if a { 1 } else { 0 }))
}
pub fn builtin_bool_to_string(args: &[Value]) -> Result<Value> {
    let a = require_bool(args, 0, "bool.ToString")?;
    Ok(Value::Str(a.to_string()))
}

pub fn builtin_char_compare_to(args: &[Value]) -> Result<Value> {
    let a = require_char(args, 0, "char.CompareTo")?;
    let b = require_char(args, 1, "char.CompareTo")?;
    Ok(Value::I64(a.cmp(&b) as i64))
}
pub fn builtin_char_equals(args: &[Value]) -> Result<Value> {
    let a = require_char(args, 0, "char.Equals")?;
    let b = require_char(args, 1, "char.Equals")?;
    Ok(Value::Bool(a == b))
}
pub fn builtin_char_hash_code(args: &[Value]) -> Result<Value> {
    let a = require_char(args, 0, "char.GetHashCode")?;
    Ok(Value::I64(a as i64))
}
pub fn builtin_char_to_string(args: &[Value]) -> Result<Value> {
    let a = require_char(args, 0, "char.ToString")?;
    Ok(Value::Str(a.to_string()))
}

pub fn builtin_str_compare_to(args: &[Value]) -> Result<Value> {
    let a = require_str(args, 0, "string.CompareTo")?;
    let b = require_str(args, 1, "string.CompareTo")?;
    Ok(Value::I64(a.cmp(&b) as i64))
}
