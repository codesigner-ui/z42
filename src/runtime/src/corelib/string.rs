use crate::metadata::Value;
use anyhow::{anyhow, bail, Result};
use super::convert::{require_str, require_usize, value_to_str};

/// Returns the number of Unicode scalar values (characters) in the string.
/// args: [this: str]
pub fn builtin_str_length(args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "__str_length")?;
    Ok(Value::I64(s.chars().count() as i64))
}

/// Returns the char at the given scalar index.
/// args: [this: str, index: i64]
/// New in simplify-string-stdlib (2026-04-24): enables script-side loops.
pub fn builtin_str_char_at(args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "__str_char_at")?;
    let i = require_usize(args, 1, "__str_char_at")?;
    s.chars().nth(i).map(Value::Char).ok_or_else(|| {
        anyhow!("__str_char_at: index {} out of range (length {})", i, s.chars().count())
    })
}

/// Builds a string from a char[] array.
/// args: [chars: Array<Char>]
/// New in simplify-string-stdlib (2026-04-24): enables script-side string
/// construction (Substring / Replace / ToLower / ToUpper etc.).
pub fn builtin_str_from_chars(args: &[Value]) -> Result<Value> {
    let arr = match args.first() {
        Some(Value::Array(a)) => a.clone(),
        Some(other) => bail!("__str_from_chars: expected char[], got {:?}", other),
        None => bail!("__str_from_chars: missing arg 0"),
    };
    let out: String = arr.borrow().iter()
        .map(|v| match v {
            Value::Char(c) => Ok(*c),
            other => Err(anyhow!("__str_from_chars: array element must be char, got {:?}", other)),
        })
        .collect::<Result<String>>()?;
    Ok(Value::Str(out))
}

// 2026-04-27 wave1-string-script: builtin_str_split + builtin_str_join removed.
// `Std.String.Split` / `Join` 现在是 z42 脚本，基于 CharAt + Substring。

// 2026-04-27 wave3a-str-concat-script: builtin_str_concat removed.
// `Std.String.Concat` 现在是 z42 脚本（用 `+` 即 IR StrConcatInstr）。

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
    Ok(Value::I64((hash & 0x7fff_ffff) as i64))
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
