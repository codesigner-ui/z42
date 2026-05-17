use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::{bail, Result};

// ── Typed argument extractors ────────────────────────────────────────────────
//
// refactor-corelib-typed-extractors (2026-05-17): direct-ABI 优化的第一阶段。
// 每个 builtin 在 dispatch 边界拿到 `&[Value]` 后会 extract typed args；旧的
// `require_str` 每次都 `s.clone()` 一份 `String`，对 `__str_length` /
// `__str_equals` 这种纯只读 ops 是显著开销。
//
// `arg_*` 系列：
//   * 返回 `&str` / `i64` / `bool` / `char` / `f64` / `usize` — 全部 borrow 或 Copy
//   * `#[inline]` 让编译器把 match 内联到 caller，消除函数调用开销
//   * 错误格式与旧 `require_*` 一致
//
// 所有 corelib 已 migrate 完，旧 `require_*` 已删（pre-1.0 不留兼容包袱）。

#[inline]
pub fn arg_str<'a>(args: &'a [Value], idx: usize, ctx: &str) -> Result<&'a str> {
    match args.get(idx) {
        Some(Value::Str(s)) => Ok(s.as_str()),
        Some(other) => bail!("{}: arg {} expected string, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

#[inline]
pub fn arg_i64(args: &[Value], idx: usize, ctx: &str) -> Result<i64> {
    match args.get(idx) {
        Some(Value::I64(n)) => Ok(*n),
        Some(other) => bail!("{}: arg {} expected int, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

#[inline]
pub fn arg_f64(args: &[Value], idx: usize, ctx: &str) -> Result<f64> {
    match args.get(idx) {
        Some(Value::F64(f)) => Ok(*f),
        Some(Value::I64(n)) => Ok(*n as f64),
        Some(other) => bail!("{}: arg {} expected double, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

#[inline]
pub fn arg_bool(args: &[Value], idx: usize, ctx: &str) -> Result<bool> {
    match args.get(idx) {
        Some(Value::Bool(b)) => Ok(*b),
        Some(other) => bail!("{}: arg {} expected bool, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

#[inline]
pub fn arg_char(args: &[Value], idx: usize, ctx: &str) -> Result<char> {
    match args.get(idx) {
        Some(Value::Char(c)) => Ok(*c),
        Some(other) => bail!("{}: arg {} expected char, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

#[inline]
pub fn arg_usize(args: &[Value], idx: usize, ctx: &str) -> Result<usize> {
    match args.get(idx) {
        Some(Value::I64(n)) if *n >= 0 => Ok(*n as usize),
        Some(other) => bail!("{}: arg {} expected non-negative integer, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

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
        Value::Closure { fn_name, .. }      => format!("<closure {fn_name}>"),
        Value::StackClosure { fn_name, .. } => format!("<closure {fn_name}>"),
        // Spec impl-ref-out-in-runtime: Refs 应该在 frame.get/set 阶段透明
        // deref，不应到达 user-visible 字符串化路径。如果出现，说明代码漏了
        // 一处 deref —— 用占位字串避免 panic，但调试时容易识别。
        Value::Ref { .. } => "<ref>".to_string(),
    }
}

// refactor-corelib-typed-extractors (2026-05-17): 旧的 `require_str` /
// `require_usize` / `to_usize` / `require_i64` / `require_f64` / `require_char`
// 全部删除 —— 全 corelib 已 migrated 到 `arg_*` 系列（零 clone / Copy / #[inline]）。
// pre-1.0 不留兼容包袱。

// ── Parse / convert builtins ─────────────────────────────────────────────────

pub fn builtin_long_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let s = arg_str(args, 0, "long.Parse")?;
    s.trim().parse::<i64>().map(Value::I64)
        .map_err(|_| anyhow::anyhow!("long.Parse: could not parse {:?} as long", s))
}

/// add-narrow-int-primitives (2026-05-15): parse the input as an i64, then
/// validate that the value fits in the target's range. Out-of-range values
/// throw OverflowException-style error (anyhow string surfaced as VM bail).
/// Pre-2026-05-15 `int.Parse("99999999999999")` silently succeeded with a
/// value larger than i32 could hold; this build now rejects it.
pub fn builtin_int_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    parse_narrow_int(args, "int.Parse", i32::MIN as i64, i32::MAX as i64)
}
pub fn builtin_i8_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    parse_narrow_int(args, "i8.Parse", i8::MIN as i64, i8::MAX as i64)
}
pub fn builtin_i16_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    parse_narrow_int(args, "i16.Parse", i16::MIN as i64, i16::MAX as i64)
}
pub fn builtin_u8_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    parse_narrow_int(args, "u8.Parse", 0, u8::MAX as i64)
}
pub fn builtin_u16_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    parse_narrow_int(args, "u16.Parse", 0, u16::MAX as i64)
}
pub fn builtin_u32_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    parse_narrow_int(args, "u32.Parse", 0, u32::MAX as i64)
}
/// u64 can hold values > i64::MAX. We parse as u64 then bit-cast to i64
/// (i.e. values above i64::MAX appear as negative under int.ToString — same
/// bit-preserving semantics as `convert_from_i64` U64 cast).
pub fn builtin_u64_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let s = arg_str(args, 0, "u64.Parse")?;
    s.trim().parse::<u64>().map(|v| Value::I64(v as i64))
        .map_err(|_| anyhow::anyhow!(
            "u64.Parse: could not parse {:?} as u64 (range: 0..={})", s, u64::MAX))
}

fn parse_narrow_int(args: &[Value], ctx: &str, min: i64, max: i64) -> Result<Value> {
    let s = arg_str(args, 0, ctx)?;
    let v = s.trim().parse::<i64>()
        .map_err(|_| anyhow::anyhow!("{}: could not parse {:?} as integer", ctx, s))?;
    if v < min || v > max {
        bail!("{}: value {} out of range (expected {}..={})", ctx, v, min, max);
    }
    Ok(Value::I64(v))
}
pub fn builtin_double_parse(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let s = arg_str(args, 0, "double.Parse")?;
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
// 旧 file-local require_* 已删 —— 用顶部 pub `arg_i64` / `arg_f64` / `arg_char`。

// 2026-04-27 wave2-compare-to-script: builtin_int_compare_to removed.
// `Std.int.CompareTo` / `Std.long.CompareTo` 现在是脚本（用 IR `<`/`>`）。

pub fn builtin_int_equals(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = arg_i64(args, 0, "int.Equals")?;
    let b = arg_i64(args, 1, "int.Equals")?;
    Ok(Value::Bool(a == b))
}
pub fn builtin_int_hash_code(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = arg_i64(args, 0, "int.GetHashCode")?;
    Ok(Value::I64(a))  // identity hash for integers
}
pub fn builtin_int_to_string(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = arg_i64(args, 0, "int.ToString")?;
    Ok(Value::Str(a.to_string()))
}

// 2026-04-27 wave2-compare-to-script: builtin_double_compare_to removed.
// `Std.double.CompareTo` / `Std.float.CompareTo` 现在是脚本（NaN → 0 由 `<`/`>` 自然返回 false 实现）。

pub fn builtin_double_equals(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = arg_f64(args, 0, "double.Equals")?;
    let b = arg_f64(args, 1, "double.Equals")?;
    Ok(Value::Bool(a == b))
}
pub fn builtin_double_hash_code(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = arg_f64(args, 0, "double.GetHashCode")?;
    Ok(Value::I64(a.to_bits() as i64))
}
pub fn builtin_double_to_string(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = arg_f64(args, 0, "double.ToString")?;
    Ok(Value::Str(a.to_string()))
}

// 2026-04-27 wave1-bool-script: 3 `builtin_bool_*` removed.
// `Std.bool.Equals` / `GetHashCode` / `ToString` 现在是 z42 脚本实现。

// 2026-04-27 wave2-compare-to-script: builtin_char_compare_to removed.
// `Std.char.CompareTo` 现在是脚本（codepoint `<`/`>` 比较）。

pub fn builtin_char_equals(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = arg_char(args, 0, "char.Equals")?;
    let b = arg_char(args, 1, "char.Equals")?;
    Ok(Value::Bool(a == b))
}
pub fn builtin_char_hash_code(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = arg_char(args, 0, "char.GetHashCode")?;
    Ok(Value::I64(a as i64))
}
pub fn builtin_char_to_string(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = arg_char(args, 0, "char.ToString")?;
    Ok(Value::Str(a.to_string()))
}

pub fn builtin_str_compare_to(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let a = arg_str(args, 0, "string.CompareTo")?;
    let b = arg_str(args, 1, "string.CompareTo")?;
    Ok(Value::I64(a.cmp(&b) as i64))
}

#[cfg(test)]
#[path = "convert_tests.rs"]
mod convert_tests;
