use crate::metadata::Value;
use anyhow::{bail, Result};
use super::convert::{require_str, require_usize, value_to_str};

/// Returns the number of Unicode scalar values (characters) in the string.
/// args: [this: str]
pub fn builtin_str_length(args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "__str_length")?;
    Ok(Value::I64(s.chars().count() as i64))
}

pub fn builtin_str_substring(args: &[Value]) -> Result<Value> {
    let s     = require_str(args, 0, "__str_substring")?;
    let start = require_usize(args, 1, "__str_substring")?;
    if args.len() == 2 {
        if start > s.len() { bail!("__str_substring: start {} out of range (len={})", start, s.len()); }
        Ok(Value::Str(s[start..].to_string()))
    } else {
        let len = require_usize(args, 2, "__str_substring")?;
        let end = start + len;
        if end > s.len() { bail!("__str_substring: range {}..{} out of range (len={})", start, end, s.len()); }
        Ok(Value::Str(s[start..end].to_string()))
    }
}

pub fn builtin_str_contains(args: &[Value]) -> Result<Value> {
    let s   = require_str(args, 0, "__str_contains")?;
    let sub = require_str(args, 1, "__str_contains")?;
    Ok(Value::Bool(s.contains(sub.as_str())))
}

pub fn builtin_str_starts_with(args: &[Value]) -> Result<Value> {
    let s      = require_str(args, 0, "__str_starts_with")?;
    let prefix = require_str(args, 1, "__str_starts_with")?;
    Ok(Value::Bool(s.starts_with(prefix.as_str())))
}

pub fn builtin_str_ends_with(args: &[Value]) -> Result<Value> {
    let s      = require_str(args, 0, "__str_ends_with")?;
    let suffix = require_str(args, 1, "__str_ends_with")?;
    Ok(Value::Bool(s.ends_with(suffix.as_str())))
}

pub fn builtin_str_index_of(args: &[Value]) -> Result<Value> {
    let s   = require_str(args, 0, "__str_index_of")?;
    let sub = require_str(args, 1, "__str_index_of")?;
    let idx = s.find(sub.as_str()).map(|i| i as i64).unwrap_or(-1);
    Ok(Value::I64(idx))
}

pub fn builtin_str_replace(args: &[Value]) -> Result<Value> {
    let s    = require_str(args, 0, "__str_replace")?;
    let from = require_str(args, 1, "__str_replace")?;
    let to   = require_str(args, 2, "__str_replace")?;
    Ok(Value::Str(s.replace(from.as_str(), to.as_str())))
}

pub fn builtin_str_to_lower(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(require_str(args, 0, "__str_to_lower")?.to_lowercase()))
}
pub fn builtin_str_to_upper(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(require_str(args, 0, "__str_to_upper")?.to_uppercase()))
}
pub fn builtin_str_trim(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(require_str(args, 0, "__str_trim")?.trim().to_string()))
}
pub fn builtin_str_trim_start(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(require_str(args, 0, "__str_trim_start")?.trim_start().to_string()))
}
pub fn builtin_str_trim_end(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(require_str(args, 0, "__str_trim_end")?.trim_end().to_string()))
}

pub fn builtin_str_is_null_or_empty(args: &[Value]) -> Result<Value> {
    Ok(Value::Bool(match args.first() {
        Some(Value::Null) | None => true,
        Some(Value::Str(s))      => s.is_empty(),
        Some(other) => bail!("string.IsNullOrEmpty: expected string or null, got {:?}", other),
    }))
}

pub fn builtin_str_is_null_or_whitespace(args: &[Value]) -> Result<Value> {
    Ok(Value::Bool(match args.first() {
        Some(Value::Null) | None => true,
        Some(Value::Str(s))      => s.trim().is_empty(),
        Some(other) => bail!("string.IsNullOrWhiteSpace: expected string or null, got {:?}", other),
    }))
}

pub fn builtin_str_split(args: &[Value]) -> Result<Value> {
    let s   = require_str(args, 0, "__str_split")?;
    let sep = require_str(args, 1, "__str_split")?;
    let parts: Vec<Value> = s.split(sep.as_str())
        .map(|p| Value::Str(p.to_string()))
        .collect();
    Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(parts))))
}

pub fn builtin_str_join(args: &[Value]) -> Result<Value> {
    if args.is_empty() { return Ok(Value::Str(String::new())); }
    let sep = match &args[0] {
        Value::Str(s) => s.as_str(),
        Value::Null   => "",
        other => bail!("string.Join: separator must be string, got {:?}", other),
    };
    let items: Vec<String> = if args.len() == 2 {
        match &args[1] {
            Value::Array(arr) => arr.borrow().iter().map(|v| value_to_str(v)).collect(),
            other             => vec![value_to_str(other)],
        }
    } else {
        args[1..].iter().map(|v| value_to_str(v)).collect()
    };
    Ok(Value::Str(items.join(sep)))
}

pub fn builtin_str_concat(args: &[Value]) -> Result<Value> {
    let mut out = String::new();
    for v in args { out.push_str(&value_to_str(v)); }
    Ok(Value::Str(out))
}

// ── Object protocol overrides for string ─────────────────────────────────────

/// string.ToString() — returns the string itself.
/// args: [this: str]
pub fn builtin_str_to_string(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(require_str(args, 0, "__str_to_string")?))
}

/// string.Equals(other) — value equality.
/// args: [this: str, other: str | null]
pub fn builtin_str_equals(args: &[Value]) -> Result<Value> {
    let a = require_str(args, 0, "__str_equals")?;
    let result = match args.get(1) {
        Some(Value::Str(b)) => a == *b,
        Some(Value::Null) | None => false,
        _ => false,
    };
    Ok(Value::Bool(result))
}

/// string.GetHashCode() — FNV-1a hash of the UTF-8 bytes.
/// args: [this: str]
pub fn builtin_str_hash_code(args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "__str_hash_code")?;
    let mut hash: u32 = 2_166_136_261;
    for byte in s.bytes() {
        hash ^= byte as u32;
        hash = hash.wrapping_mul(16_777_619);
    }
    Ok(Value::I32((hash & 0x7fff_ffff) as i32))
}

pub fn builtin_str_format(args: &[Value]) -> Result<Value> {
    if args.is_empty() { return Ok(Value::Str(String::new())); }
    let template = require_str(args, 0, "string.Format")?;
    let mut result = template.to_string();
    for (i, arg) in args[1..].iter().enumerate() {
        result = result.replace(&format!("{{{}}}", i), &value_to_str(arg));
    }
    Ok(Value::Str(result))
}
