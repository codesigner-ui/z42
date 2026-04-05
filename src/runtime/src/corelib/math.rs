use crate::metadata::Value;
use anyhow::{bail, Result};

pub fn builtin_math_abs(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::I64(n)) => Ok(Value::I64(n.abs())),
        Some(Value::F64(f)) => Ok(Value::F64(f.abs())),
        Some(other) => bail!("Math.Abs: unsupported type {:?}", other),
        None => bail!("Math.Abs: missing argument"),
    }
}
pub fn builtin_math_max(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::I64(a)), Some(Value::I64(b))) => Ok(Value::I64(*a.max(b))),
        (Some(Value::F64(a)), Some(Value::F64(b))) => Ok(Value::F64(a.max(*b))),
        _ => bail!("Math.Max: expected two numeric arguments"),
    }
}
pub fn builtin_math_min(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::I64(a)), Some(Value::I64(b))) => Ok(Value::I64(*a.min(b))),
        (Some(Value::F64(a)), Some(Value::F64(b))) => Ok(Value::F64(a.min(*b))),
        _ => bail!("Math.Min: expected two numeric arguments"),
    }
}
pub fn builtin_math_pow(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::I64(base)), Some(Value::I64(exp))) => Ok(Value::I64(base.pow(*exp as u32))),
        (Some(Value::F64(base)), Some(Value::F64(exp))) => Ok(Value::F64(base.powf(*exp))),
        _ => bail!("Math.Pow: expected two numeric arguments"),
    }
}
pub fn builtin_math_sqrt(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.sqrt())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).sqrt())),
        _ => bail!("Math.Sqrt: expected numeric argument"),
    }
}
pub fn builtin_math_floor(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.floor())),
        Some(Value::I64(n)) => Ok(Value::I64(*n)),
        _ => bail!("Math.Floor: expected numeric argument"),
    }
}
pub fn builtin_math_ceiling(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.ceil())),
        Some(Value::I64(n)) => Ok(Value::I64(*n)),
        _ => bail!("Math.Ceiling: expected numeric argument"),
    }
}
pub fn builtin_math_round(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.round())),
        Some(Value::I64(n)) => Ok(Value::I64(*n)),
        _ => bail!("Math.Round: expected numeric argument"),
    }
}
pub fn builtin_math_log(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.ln())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).ln())),
        _ => bail!("Math.Log: expected numeric argument"),
    }
}
pub fn builtin_math_log10(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.log10())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).log10())),
        _ => bail!("Math.Log10: expected numeric argument"),
    }
}
pub fn builtin_math_sin(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.sin())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).sin())),
        _ => bail!("Math.Sin: expected numeric argument"),
    }
}
pub fn builtin_math_cos(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.cos())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).cos())),
        _ => bail!("Math.Cos: expected numeric argument"),
    }
}
pub fn builtin_math_tan(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.tan())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).tan())),
        _ => bail!("Math.Tan: expected numeric argument"),
    }
}
pub fn builtin_math_atan2(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::F64(y)), Some(Value::F64(x))) => Ok(Value::F64(y.atan2(*x))),
        _ => bail!("Math.Atan2: expected two f64 arguments"),
    }
}
pub fn builtin_math_exp(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.exp())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).exp())),
        _ => bail!("Math.Exp: expected numeric argument"),
    }
}
