use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::{bail, Result};
use super::convert::value_to_str;

pub fn builtin_println(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let text = args.first().map(value_to_str).unwrap_or_default();
    println!("{}", text);
    Ok(Value::Null)
}

pub fn builtin_print(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let text = args.first().map(value_to_str).unwrap_or_default();
    print!("{}", text);
    Ok(Value::Null)
}

pub fn builtin_readline(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let mut line = String::new();
    std::io::stdin().read_line(&mut line)?;
    Ok(Value::Str(line.trim_end_matches(['\n', '\r']).to_string()))
}

pub fn builtin_concat(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = args.first().map(value_to_str).unwrap_or_default();
    let b = args.get(1).map(value_to_str).unwrap_or_default();
    Ok(Value::Str(format!("{}{}", a, b)))
}

pub fn builtin_len(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(rc)) => Ok(Value::I64(rc.borrow().len() as i64)),
        Some(Value::Str(s))    => Ok(Value::I64(s.len() as i64)),
        Some(other)            => bail!("__len: expected array or string, got {:?}", other),
        None                   => bail!("__len: missing argument"),
    }
}

pub fn builtin_contains(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Str(s)) => {
            use super::convert::require_str;
            let needle = require_str(args, 1, "__contains")?;
            Ok(Value::Bool(s.contains(needle.as_str())))
        }
        Some(Value::Array(arr)) => {
            let item = args.get(1).cloned().unwrap_or(Value::Null);
            Ok(Value::Bool(arr.borrow().iter().any(|v| v == &item)))
        }
        _ => bail!("Contains: first argument must be a string or List"),
    }
}
