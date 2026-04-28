use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::{bail, Result};

// 2026-04-27 wave1-math-script: builtin_math_abs/_max/_min removed.
// `Std.Math.Math.Abs/Max/Min` 现在是 z42 脚本（int + double overload）。

pub fn builtin_math_pow(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::I64(base)), Some(Value::I64(exp))) => Ok(Value::I64(base.pow(*exp as u32))),
        (Some(Value::F64(base)), Some(Value::F64(exp))) => Ok(Value::F64(base.powf(*exp))),
        _ => bail!("Math.Pow: expected two numeric arguments"),
    }
}
pub fn builtin_math_sqrt(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.sqrt())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).sqrt())),
        _ => bail!("Math.Sqrt: expected numeric argument"),
    }
}
pub fn builtin_math_floor(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.floor())),
        Some(Value::I64(n)) => Ok(Value::I64(*n)),
        _ => bail!("Math.Floor: expected numeric argument"),
    }
}
pub fn builtin_math_ceiling(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.ceil())),
        Some(Value::I64(n)) => Ok(Value::I64(*n)),
        _ => bail!("Math.Ceiling: expected numeric argument"),
    }
}
pub fn builtin_math_round(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.round())),
        Some(Value::I64(n)) => Ok(Value::I64(*n)),
        _ => bail!("Math.Round: expected numeric argument"),
    }
}
pub fn builtin_math_log(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.ln())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).ln())),
        _ => bail!("Math.Log: expected numeric argument"),
    }
}
pub fn builtin_math_log10(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.log10())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).log10())),
        _ => bail!("Math.Log10: expected numeric argument"),
    }
}
pub fn builtin_math_sin(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.sin())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).sin())),
        _ => bail!("Math.Sin: expected numeric argument"),
    }
}
pub fn builtin_math_cos(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.cos())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).cos())),
        _ => bail!("Math.Cos: expected numeric argument"),
    }
}
pub fn builtin_math_tan(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.tan())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).tan())),
        _ => bail!("Math.Tan: expected numeric argument"),
    }
}
pub fn builtin_math_atan2(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::F64(y)), Some(Value::F64(x))) => Ok(Value::F64(y.atan2(*x))),
        _ => bail!("Math.Atan2: expected two f64 arguments"),
    }
}
pub fn builtin_math_exp(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.exp())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).exp())),
        _ => bail!("Math.Exp: expected numeric argument"),
    }
}
