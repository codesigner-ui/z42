use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::{bail, Result};

/// Convert a Value to its string representation.
///
/// Exhaustive match: 加新 `Value` variant 时编译期强制覆盖（防止再次出现
/// 像 `Value::Map` 那样"variant 加进 enum 但消费侧忘记更新"的死代码）。
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
        Value::PinnedView { ptr, len, kind } => {
            format!("PinnedView{{ptr=0x{ptr:x}, len={len}, kind={kind:?}}}")
        }
        Value::FuncRef(name) => format!("<fn {name}>"),
        Value::Closure { fn_name, .. } => format!("<closure {fn_name}>"),
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

pub fn builtin_long_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "long.Parse")?;
    s.trim().parse::<i64>().map(Value::I64)
        .map_err(|_| anyhow::anyhow!("long.Parse: could not parse {:?} as long", s))
}
pub fn builtin_int_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "int.Parse")?;
    s.trim().parse::<i64>().map(Value::I64)
        .map_err(|_| anyhow::anyhow!("int.Parse: could not parse {:?} as int", s))
}
pub fn builtin_double_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "double.Parse")?;
    s.trim().parse::<f64>().map(Value::F64)
        .map_err(|_| anyhow::anyhow!("double.Parse: could not parse {:?} as double", s))
}
pub fn builtin_to_str(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
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
fn require_char(args: &[Value], idx: usize, ctx: &str) -> Result<char> {
    match args.get(idx) {
        Some(Value::Char(c)) => Ok(*c),
        Some(other) => bail!("{}: arg {} expected char, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

// 2026-04-27 wave2-compare-to-script: builtin_int_compare_to removed.
// `Std.int.CompareTo` / `Std.long.CompareTo` 现在是脚本（用 IR `<`/`>`）。

pub fn builtin_int_equals(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = require_i64(args, 0, "int.Equals")?;
    let b = require_i64(args, 1, "int.Equals")?;
    Ok(Value::Bool(a == b))
}
pub fn builtin_int_hash_code(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = require_i64(args, 0, "int.GetHashCode")?;
    Ok(Value::I64(a))  // identity hash for integers
}
pub fn builtin_int_to_string(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = require_i64(args, 0, "int.ToString")?;
    Ok(Value::Str(a.to_string()))
}

// 2026-04-27 wave2-compare-to-script: builtin_double_compare_to removed.
// `Std.double.CompareTo` / `Std.float.CompareTo` 现在是脚本（NaN → 0 由 `<`/`>` 自然返回 false 实现）。

pub fn builtin_double_equals(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = require_f64(args, 0, "double.Equals")?;
    let b = require_f64(args, 1, "double.Equals")?;
    Ok(Value::Bool(a == b))
}
pub fn builtin_double_hash_code(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = require_f64(args, 0, "double.GetHashCode")?;
    Ok(Value::I64(a.to_bits() as i64))
}
pub fn builtin_double_to_string(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = require_f64(args, 0, "double.ToString")?;
    Ok(Value::Str(a.to_string()))
}

// 2026-04-27 wave1-bool-script: 3 `builtin_bool_*` removed.
// `Std.bool.Equals` / `GetHashCode` / `ToString` 现在是 z42 脚本实现。

// 2026-04-27 wave2-compare-to-script: builtin_char_compare_to removed.
// `Std.char.CompareTo` 现在是脚本（codepoint `<`/`>` 比较）。

pub fn builtin_char_equals(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = require_char(args, 0, "char.Equals")?;
    let b = require_char(args, 1, "char.Equals")?;
    Ok(Value::Bool(a == b))
}
pub fn builtin_char_hash_code(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = require_char(args, 0, "char.GetHashCode")?;
    Ok(Value::I64(a as i64))
}
pub fn builtin_char_to_string(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = require_char(args, 0, "char.ToString")?;
    Ok(Value::Str(a.to_string()))
}

pub fn builtin_str_compare_to(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = require_str(args, 0, "string.CompareTo")?;
    let b = require_str(args, 1, "string.CompareTo")?;
    Ok(Value::I64(a.cmp(&b) as i64))
}
