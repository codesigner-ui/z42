/// Builtin function dispatch — called by both the interpreter (via `Builtin` instructions)
/// and the JIT backend (via `jit_builtin` extern "C" helper in jit/helpers.rs).
///
/// Architecture:
///   exec_builtin(name, args)  ← single public entry point, unchanged API
///       └─ dispatch_table()   ← OnceLock<HashMap>, built once at first call
///           └─ individual fn  ← one fn per intrinsic, all private

use crate::types::Value;
use anyhow::{bail, Result};
use std::collections::HashMap;
use std::sync::OnceLock;
use super::helpers::{require_str, require_usize, value_to_str};

// ── Dispatch table ────────────────────────────────────────────────────────────

pub type NativeFn = fn(&[Value]) -> Result<Value>;

static DISPATCH: OnceLock<HashMap<&'static str, NativeFn>> = OnceLock::new();

fn dispatch_table() -> &'static HashMap<&'static str, NativeFn> {
    DISPATCH.get_or_init(|| {
        let mut m: HashMap<&'static str, NativeFn> = HashMap::new();
        // I/O
        m.insert("__println",  builtin_println);
        m.insert("__print",    builtin_print);
        m.insert("__readline", builtin_readline);
        m.insert("__concat",   builtin_concat);
        // Length
        m.insert("__len",      builtin_len);
        // String
        m.insert("__str_substring",             builtin_str_substring);
        m.insert("__str_contains",              builtin_str_contains);
        m.insert("__str_starts_with",           builtin_str_starts_with);
        m.insert("__str_ends_with",             builtin_str_ends_with);
        m.insert("__str_index_of",              builtin_str_index_of);
        m.insert("__str_replace",               builtin_str_replace);
        m.insert("__str_to_lower",              builtin_str_to_lower);
        m.insert("__str_to_upper",              builtin_str_to_upper);
        m.insert("__str_trim",                  builtin_str_trim);
        m.insert("__str_trim_start",            builtin_str_trim_start);
        m.insert("__str_trim_end",              builtin_str_trim_end);
        m.insert("__str_is_null_or_empty",      builtin_str_is_null_or_empty);
        m.insert("__str_is_null_or_whitespace", builtin_str_is_null_or_whitespace);
        m.insert("__str_split",                 builtin_str_split);
        m.insert("__str_join",                  builtin_str_join);
        m.insert("__str_concat",                builtin_str_concat);
        m.insert("__str_format",                builtin_str_format);
        m.insert("__contains",                  builtin_contains);
        // Parse / convert
        m.insert("__long_parse",   builtin_long_parse);
        m.insert("__int_parse",    builtin_int_parse);
        m.insert("__double_parse", builtin_double_parse);
        m.insert("__to_str",       builtin_to_str);
        // Assert
        m.insert("__assert_eq",       builtin_assert_eq);
        m.insert("__assert_true",     builtin_assert_true);
        m.insert("__assert_false",    builtin_assert_false);
        m.insert("__assert_null",     builtin_assert_null);
        m.insert("__assert_not_null", builtin_assert_not_null);
        m.insert("__assert_contains", builtin_assert_contains);
        // Math
        m.insert("__math_abs",     builtin_math_abs);
        m.insert("__math_max",     builtin_math_max);
        m.insert("__math_min",     builtin_math_min);
        m.insert("__math_pow",     builtin_math_pow);
        m.insert("__math_sqrt",    builtin_math_sqrt);
        m.insert("__math_floor",   builtin_math_floor);
        m.insert("__math_ceiling", builtin_math_ceiling);
        m.insert("__math_round",   builtin_math_round);
        m.insert("__math_log",     builtin_math_log);
        m.insert("__math_log10",   builtin_math_log10);
        m.insert("__math_sin",     builtin_math_sin);
        m.insert("__math_cos",     builtin_math_cos);
        m.insert("__math_tan",     builtin_math_tan);
        m.insert("__math_atan2",   builtin_math_atan2);
        m.insert("__math_exp",     builtin_math_exp);
        // List
        m.insert("__list_new",       builtin_list_new);
        m.insert("__list_add",       builtin_list_add);
        m.insert("__list_remove_at", builtin_list_remove_at);
        m.insert("__list_contains",  builtin_list_contains);
        m.insert("__list_clear",     builtin_list_clear);
        m.insert("__list_insert",    builtin_list_insert);
        m.insert("__list_sort",      builtin_list_sort);
        m.insert("__list_reverse",   builtin_list_reverse);
        // Dictionary
        m.insert("__dict_new",          builtin_dict_new);
        m.insert("__dict_contains_key", builtin_dict_contains_key);
        m.insert("__dict_remove",       builtin_dict_remove);
        m.insert("__dict_keys",         builtin_dict_keys);
        m.insert("__dict_values",       builtin_dict_values);
        // File I/O
        m.insert("__file_read_text",   builtin_file_read_text);
        m.insert("__file_write_text",  builtin_file_write_text);
        m.insert("__file_append_text", builtin_file_append_text);
        m.insert("__file_exists",      builtin_file_exists);
        m.insert("__file_delete",      builtin_file_delete);
        // Path
        m.insert("__path_join",                     builtin_path_join);
        m.insert("__path_get_extension",            builtin_path_get_extension);
        m.insert("__path_get_filename",             builtin_path_get_filename);
        m.insert("__path_get_directory",            builtin_path_get_directory);
        m.insert("__path_get_filename_without_ext", builtin_path_get_filename_without_ext);
        // Environment / process
        m.insert("__env_get",      builtin_env_get);
        m.insert("__env_args",     builtin_env_args);
        m.insert("__process_exit", builtin_process_exit);
        m.insert("__time_now_ms",  builtin_time_now_ms);
        m
    })
}

/// Public entry point — stable API used by both the interpreter and JIT `jit_builtin`.
pub fn exec_builtin(name: &str, args: &[Value]) -> Result<Value> {
    dispatch_table()
        .get(name)
        .ok_or_else(|| anyhow::anyhow!("unknown builtin `{name}`"))?
        (args)
}

// ── I/O ───────────────────────────────────────────────────────────────────────

fn builtin_println(args: &[Value]) -> Result<Value> {
    let text = args.first().map(value_to_str).unwrap_or_default();
    println!("{}", text);
    Ok(Value::Null)
}

fn builtin_print(args: &[Value]) -> Result<Value> {
    let text = args.first().map(value_to_str).unwrap_or_default();
    print!("{}", text);
    Ok(Value::Null)
}

fn builtin_readline(_args: &[Value]) -> Result<Value> {
    let mut line = String::new();
    std::io::stdin().read_line(&mut line)?;
    Ok(Value::Str(line.trim_end_matches(['\n', '\r']).to_string()))
}

fn builtin_concat(args: &[Value]) -> Result<Value> {
    let a = args.first().map(value_to_str).unwrap_or_default();
    let b = args.get(1).map(value_to_str).unwrap_or_default();
    Ok(Value::Str(format!("{}{}", a, b)))
}

// ── Length ────────────────────────────────────────────────────────────────────

fn builtin_len(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(rc)) => Ok(Value::I64(rc.borrow().len() as i64)),
        Some(Value::Str(s))    => Ok(Value::I64(s.len() as i64)),
        Some(Value::Map(rc))   => Ok(Value::I64(rc.borrow().len() as i64)),
        Some(other)            => bail!("__len: expected array, string, or map, got {:?}", other),
        None                   => bail!("__len: missing argument"),
    }
}

// ── String ────────────────────────────────────────────────────────────────────

fn builtin_str_substring(args: &[Value]) -> Result<Value> {
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

fn builtin_str_contains(args: &[Value]) -> Result<Value> {
    let s   = require_str(args, 0, "__str_contains")?;
    let sub = require_str(args, 1, "__str_contains")?;
    Ok(Value::Bool(s.contains(sub.as_str())))
}

fn builtin_str_starts_with(args: &[Value]) -> Result<Value> {
    let s      = require_str(args, 0, "__str_starts_with")?;
    let prefix = require_str(args, 1, "__str_starts_with")?;
    Ok(Value::Bool(s.starts_with(prefix.as_str())))
}

fn builtin_str_ends_with(args: &[Value]) -> Result<Value> {
    let s      = require_str(args, 0, "__str_ends_with")?;
    let suffix = require_str(args, 1, "__str_ends_with")?;
    Ok(Value::Bool(s.ends_with(suffix.as_str())))
}

fn builtin_str_index_of(args: &[Value]) -> Result<Value> {
    let s   = require_str(args, 0, "__str_index_of")?;
    let sub = require_str(args, 1, "__str_index_of")?;
    let idx = s.find(sub.as_str()).map(|i| i as i64).unwrap_or(-1);
    Ok(Value::I64(idx))
}

fn builtin_str_replace(args: &[Value]) -> Result<Value> {
    let s    = require_str(args, 0, "__str_replace")?;
    let from = require_str(args, 1, "__str_replace")?;
    let to   = require_str(args, 2, "__str_replace")?;
    Ok(Value::Str(s.replace(from.as_str(), to.as_str())))
}

fn builtin_str_to_lower(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(require_str(args, 0, "__str_to_lower")?.to_lowercase()))
}
fn builtin_str_to_upper(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(require_str(args, 0, "__str_to_upper")?.to_uppercase()))
}
fn builtin_str_trim(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(require_str(args, 0, "__str_trim")?.trim().to_string()))
}
fn builtin_str_trim_start(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(require_str(args, 0, "__str_trim_start")?.trim_start().to_string()))
}
fn builtin_str_trim_end(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(require_str(args, 0, "__str_trim_end")?.trim_end().to_string()))
}

fn builtin_str_is_null_or_empty(args: &[Value]) -> Result<Value> {
    Ok(Value::Bool(match args.first() {
        Some(Value::Null) | None => true,
        Some(Value::Str(s))      => s.is_empty(),
        Some(other) => bail!("string.IsNullOrEmpty: expected string or null, got {:?}", other),
    }))
}

fn builtin_str_is_null_or_whitespace(args: &[Value]) -> Result<Value> {
    Ok(Value::Bool(match args.first() {
        Some(Value::Null) | None => true,
        Some(Value::Str(s))      => s.trim().is_empty(),
        Some(other) => bail!("string.IsNullOrWhiteSpace: expected string or null, got {:?}", other),
    }))
}

fn builtin_str_split(args: &[Value]) -> Result<Value> {
    let s   = require_str(args, 0, "__str_split")?;
    let sep = require_str(args, 1, "__str_split")?;
    let parts: Vec<Value> = s.split(sep.as_str())
        .map(|p| Value::Str(p.to_string()))
        .collect();
    Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(parts))))
}

fn builtin_str_join(args: &[Value]) -> Result<Value> {
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

fn builtin_str_concat(args: &[Value]) -> Result<Value> {
    let mut out = String::new();
    for v in args { out.push_str(&value_to_str(v)); }
    Ok(Value::Str(out))
}

fn builtin_str_format(args: &[Value]) -> Result<Value> {
    if args.is_empty() { return Ok(Value::Str(String::new())); }
    let template = require_str(args, 0, "string.Format")?;
    let mut result = template.to_string();
    for (i, arg) in args[1..].iter().enumerate() {
        result = result.replace(&format!("{{{}}}", i), &value_to_str(arg));
    }
    Ok(Value::Str(result))
}

fn builtin_contains(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Str(s)) => {
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

// ── Parse / convert ───────────────────────────────────────────────────────────

fn builtin_long_parse(args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "long.Parse")?;
    s.trim().parse::<i64>().map(Value::I64)
        .map_err(|_| anyhow::anyhow!("long.Parse: could not parse {:?} as long", s))
}
fn builtin_int_parse(args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "int.Parse")?;
    s.trim().parse::<i64>().map(Value::I64)
        .map_err(|_| anyhow::anyhow!("int.Parse: could not parse {:?} as int", s))
}
fn builtin_double_parse(args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "double.Parse")?;
    s.trim().parse::<f64>().map(Value::F64)
        .map_err(|_| anyhow::anyhow!("double.Parse: could not parse {:?} as double", s))
}
fn builtin_to_str(args: &[Value]) -> Result<Value> {
    Ok(Value::Str(args.first().map(value_to_str).unwrap_or_default()))
}

// ── Assert ────────────────────────────────────────────────────────────────────

fn builtin_assert_eq(args: &[Value]) -> Result<Value> {
    let expected = args.first().cloned().unwrap_or(Value::Null);
    let actual   = args.get(1).cloned().unwrap_or(Value::Null);
    if expected != actual {
        bail!("AssertionError: expected {} but got {}",
            value_to_str(&expected), value_to_str(&actual));
    }
    Ok(Value::Null)
}
fn builtin_assert_true(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Bool(true)) => Ok(Value::Null),
        Some(other) => bail!("AssertionError: expected true but got {}", value_to_str(other)),
        None        => bail!("AssertionError: __assert_true missing argument"),
    }
}
fn builtin_assert_false(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Bool(false)) => Ok(Value::Null),
        Some(other) => bail!("AssertionError: expected false but got {}", value_to_str(other)),
        None        => bail!("AssertionError: __assert_false missing argument"),
    }
}
fn builtin_assert_null(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Null) | None => Ok(Value::Null),
        Some(other) => bail!("AssertionError: expected null but got {}", value_to_str(other)),
    }
}
fn builtin_assert_not_null(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Null) | None => bail!("AssertionError: expected non-null but got null"),
        Some(_) => Ok(Value::Null),
    }
}
fn builtin_assert_contains(args: &[Value]) -> Result<Value> {
    let sub = require_str(args, 0, "__assert_contains")?;
    let s   = require_str(args, 1, "__assert_contains")?;
    if !s.contains(sub.as_str()) {
        bail!("AssertionError: expected {:?} to contain {:?}", s, sub);
    }
    Ok(Value::Null)
}

// ── Math ──────────────────────────────────────────────────────────────────────

fn builtin_math_abs(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::I64(n)) => Ok(Value::I64(n.abs())),
        Some(Value::F64(f)) => Ok(Value::F64(f.abs())),
        Some(other) => bail!("Math.Abs: unsupported type {:?}", other),
        None => bail!("Math.Abs: missing argument"),
    }
}
fn builtin_math_max(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::I64(a)), Some(Value::I64(b))) => Ok(Value::I64(*a.max(b))),
        (Some(Value::F64(a)), Some(Value::F64(b))) => Ok(Value::F64(a.max(*b))),
        _ => bail!("Math.Max: expected two numeric arguments"),
    }
}
fn builtin_math_min(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::I64(a)), Some(Value::I64(b))) => Ok(Value::I64(*a.min(b))),
        (Some(Value::F64(a)), Some(Value::F64(b))) => Ok(Value::F64(a.min(*b))),
        _ => bail!("Math.Min: expected two numeric arguments"),
    }
}
fn builtin_math_pow(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::I64(base)), Some(Value::I64(exp))) => Ok(Value::I64(base.pow(*exp as u32))),
        (Some(Value::F64(base)), Some(Value::F64(exp))) => Ok(Value::F64(base.powf(*exp))),
        _ => bail!("Math.Pow: expected two numeric arguments"),
    }
}
fn builtin_math_sqrt(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.sqrt())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).sqrt())),
        _ => bail!("Math.Sqrt: expected numeric argument"),
    }
}
fn builtin_math_floor(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.floor())),
        Some(Value::I64(n)) => Ok(Value::I64(*n)),
        _ => bail!("Math.Floor: expected numeric argument"),
    }
}
fn builtin_math_ceiling(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.ceil())),
        Some(Value::I64(n)) => Ok(Value::I64(*n)),
        _ => bail!("Math.Ceiling: expected numeric argument"),
    }
}
fn builtin_math_round(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.round())),
        Some(Value::I64(n)) => Ok(Value::I64(*n)),
        _ => bail!("Math.Round: expected numeric argument"),
    }
}
fn builtin_math_log(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.ln())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).ln())),
        _ => bail!("Math.Log: expected numeric argument"),
    }
}
fn builtin_math_log10(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.log10())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).log10())),
        _ => bail!("Math.Log10: expected numeric argument"),
    }
}
fn builtin_math_sin(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.sin())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).sin())),
        _ => bail!("Math.Sin: expected numeric argument"),
    }
}
fn builtin_math_cos(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.cos())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).cos())),
        _ => bail!("Math.Cos: expected numeric argument"),
    }
}
fn builtin_math_tan(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.tan())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).tan())),
        _ => bail!("Math.Tan: expected numeric argument"),
    }
}
fn builtin_math_atan2(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::F64(y)), Some(Value::F64(x))) => Ok(Value::F64(y.atan2(*x))),
        _ => bail!("Math.Atan2: expected two f64 arguments"),
    }
}
fn builtin_math_exp(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::F64(f)) => Ok(Value::F64(f.exp())),
        Some(Value::I64(n)) => Ok(Value::F64((*n as f64).exp())),
        _ => bail!("Math.Exp: expected numeric argument"),
    }
}

// ── List ──────────────────────────────────────────────────────────────────────

fn builtin_list_new(_args: &[Value]) -> Result<Value> {
    Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(vec![]))))
}
fn builtin_list_add(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(arr)) => {
            arr.borrow_mut().push(args.get(1).cloned().unwrap_or(Value::Null));
            Ok(Value::Null)
        }
        _ => bail!("List.Add: first argument must be a List"),
    }
}
fn builtin_list_remove_at(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::Array(arr)), Some(Value::I64(idx))) => {
            let idx = *idx as usize;
            let mut v = arr.borrow_mut();
            if idx >= v.len() { bail!("List.RemoveAt: index {} out of range (len={})", idx, v.len()); }
            v.remove(idx);
            Ok(Value::Null)
        }
        _ => bail!("List.RemoveAt: expected (List, i64)"),
    }
}
fn builtin_list_contains(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(arr)) => {
            let item = args.get(1).cloned().unwrap_or(Value::Null);
            Ok(Value::Bool(arr.borrow().iter().any(|v| v == &item)))
        }
        _ => bail!("List.Contains: first argument must be a List"),
    }
}
fn builtin_list_clear(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(arr)) => { arr.borrow_mut().clear(); Ok(Value::Null) }
        _ => bail!("List.Clear: first argument must be a List"),
    }
}
fn builtin_list_insert(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1), args.get(2)) {
        (Some(Value::Array(arr)), Some(Value::I64(idx)), Some(item)) => {
            let idx  = *idx as usize;
            let item = item.clone();
            let mut v = arr.borrow_mut();
            if idx > v.len() { bail!("List.Insert: index {} out of range (len={})", idx, v.len()); }
            v.insert(idx, item);
            Ok(Value::Null)
        }
        _ => bail!("List.Insert: expected (List, i64, value)"),
    }
}
fn builtin_list_sort(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(arr)) => {
            arr.borrow_mut().sort_by(|a, b| match (a, b) {
                (Value::I64(x), Value::I64(y)) => x.cmp(y),
                (Value::F64(x), Value::F64(y)) => x.partial_cmp(y).unwrap_or(std::cmp::Ordering::Equal),
                (Value::Str(x), Value::Str(y)) => x.cmp(y),
                _ => std::cmp::Ordering::Equal,
            });
            Ok(Value::Null)
        }
        _ => bail!("List.Sort: first argument must be a List"),
    }
}
fn builtin_list_reverse(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(arr)) => { arr.borrow_mut().reverse(); Ok(Value::Null) }
        _ => bail!("List.Reverse: first argument must be a List"),
    }
}

// ── Dictionary ────────────────────────────────────────────────────────────────

fn builtin_dict_new(_args: &[Value]) -> Result<Value> {
    Ok(Value::Map(std::rc::Rc::new(std::cell::RefCell::new(std::collections::HashMap::new()))))
}
fn builtin_dict_contains_key(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::Map(rc)), Some(key)) =>
            Ok(Value::Bool(rc.borrow().contains_key(&value_to_str(key)))),
        _ => bail!("Dictionary.ContainsKey: expected (Dictionary, key)"),
    }
}
fn builtin_dict_remove(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::Array(arr)), Some(item)) => {
            let mut v = arr.borrow_mut();
            let removed = v.iter().position(|x| x == item).map(|p| { v.remove(p); true }).unwrap_or(false);
            Ok(Value::Bool(removed))
        }
        (Some(Value::Map(rc)), Some(key)) => {
            Ok(Value::Bool(rc.borrow_mut().remove(&value_to_str(key)).is_some()))
        }
        _ => bail!("Remove: expected (List, value) or (Dictionary, key)"),
    }
}
fn builtin_dict_keys(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Map(rc)) => {
            let keys: Vec<Value> = rc.borrow().keys().map(|k| Value::Str(k.clone())).collect();
            Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(keys))))
        }
        _ => bail!("Dictionary.Keys: expected Dictionary"),
    }
}
fn builtin_dict_values(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Map(rc)) => {
            let vals: Vec<Value> = rc.borrow().values().cloned().collect();
            Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(vals))))
        }
        _ => bail!("Dictionary.Values: expected Dictionary"),
    }
}

// ── File I/O ──────────────────────────────────────────────────────────────────

fn builtin_file_read_text(args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__file_read_text")?;
    let text = std::fs::read_to_string(path.as_str())?;
    Ok(Value::Str(text))
}
fn builtin_file_write_text(args: &[Value]) -> Result<Value> {
    let path    = require_str(args, 0, "__file_write_text")?;
    let content = require_str(args, 1, "__file_write_text")?;
    std::fs::write(path.as_str(), content.as_str())?;
    Ok(Value::Null)
}
fn builtin_file_append_text(args: &[Value]) -> Result<Value> {
    use std::io::Write;
    let path    = require_str(args, 0, "__file_append_text")?;
    let content = require_str(args, 1, "__file_append_text")?;
    let mut file = std::fs::OpenOptions::new().append(true).create(true).open(path.as_str())?;
    file.write_all(content.as_bytes())?;
    Ok(Value::Null)
}
fn builtin_file_exists(args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__file_exists")?;
    Ok(Value::Bool(std::path::Path::new(path.as_str()).exists()))
}
fn builtin_file_delete(args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__file_delete")?;
    std::fs::remove_file(path.as_str())?;
    Ok(Value::Null)
}

// ── Path ──────────────────────────────────────────────────────────────────────

fn builtin_path_join(args: &[Value]) -> Result<Value> {
    let a = require_str(args, 0, "__path_join")?;
    let b = require_str(args, 1, "__path_join")?;
    Ok(Value::Str(std::path::Path::new(a.as_str()).join(b.as_str()).to_string_lossy().into_owned()))
}
fn builtin_path_get_extension(args: &[Value]) -> Result<Value> {
    let p = require_str(args, 0, "__path_get_extension")?;
    Ok(Value::Str(std::path::Path::new(p.as_str()).extension()
        .and_then(|e| e.to_str()).unwrap_or("").to_string()))
}
fn builtin_path_get_filename(args: &[Value]) -> Result<Value> {
    let p = require_str(args, 0, "__path_get_filename")?;
    Ok(Value::Str(std::path::Path::new(p.as_str()).file_name()
        .and_then(|n| n.to_str()).unwrap_or("").to_string()))
}
fn builtin_path_get_directory(args: &[Value]) -> Result<Value> {
    let p = require_str(args, 0, "__path_get_directory")?;
    Ok(Value::Str(std::path::Path::new(p.as_str()).parent()
        .and_then(|d| d.to_str()).unwrap_or("").to_string()))
}
fn builtin_path_get_filename_without_ext(args: &[Value]) -> Result<Value> {
    let p = require_str(args, 0, "__path_get_filename_without_ext")?;
    Ok(Value::Str(std::path::Path::new(p.as_str()).file_stem()
        .and_then(|n| n.to_str()).unwrap_or("").to_string()))
}

// ── Environment / Process ─────────────────────────────────────────────────────

fn builtin_env_get(args: &[Value]) -> Result<Value> {
    let key = require_str(args, 0, "__env_get")?;
    Ok(match std::env::var(key.as_str()) {
        Ok(v)  => Value::Str(v),
        Err(_) => Value::Null,
    })
}
fn builtin_env_args(_args: &[Value]) -> Result<Value> {
    let list: Vec<Value> = std::env::args().map(Value::Str).collect();
    Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(list))))
}
fn builtin_process_exit(args: &[Value]) -> Result<Value> {
    let code = match args.first() {
        Some(Value::I64(n)) => *n as i32,
        Some(Value::I32(n)) => *n,
        _ => 0,
    };
    std::process::exit(code);
}
fn builtin_time_now_ms(_args: &[Value]) -> Result<Value> {
    let ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0);
    Ok(Value::I64(ms))
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    fn s(v: &str) -> Value { Value::Str(v.into()) }
    fn i(n: i32) -> Value { Value::I32(n) }
    fn i64(n: i64) -> Value { Value::I64(n) }

    // ── __len ─────────────────────────────────────────────────────────────────

    #[test]
    fn len_of_string_is_utf8_bytes() {
        assert_eq!(exec_builtin("__len", &[s("hello")]).unwrap(), i64(5));
    }

    #[test]
    fn len_of_empty_string() {
        assert_eq!(exec_builtin("__len", &[s("")]).unwrap(), i64(0));
    }

    #[test]
    fn len_missing_arg_errors() {
        assert!(exec_builtin("__len", &[]).is_err());
    }

    // ── __str_substring ───────────────────────────────────────────────────────

    #[test]
    fn substring_one_arg() {
        assert_eq!(
            exec_builtin("__str_substring", &[s("Hello, World!"), i(7)]).unwrap(),
            s("World!")
        );
    }

    #[test]
    fn substring_two_args() {
        assert_eq!(
            exec_builtin("__str_substring", &[s("Hello, World!"), i(7), i(5)]).unwrap(),
            s("World")
        );
    }

    #[test]
    fn substring_out_of_range_errors() {
        assert!(exec_builtin("__str_substring", &[s("hi"), i(10)]).is_err());
    }

    // ── __str_contains ────────────────────────────────────────────────────────

    #[test]
    fn contains_true() {
        assert_eq!(
            exec_builtin("__str_contains", &[s("Hello, World!"), s("World")]).unwrap(),
            Value::Bool(true)
        );
    }

    #[test]
    fn contains_false() {
        assert_eq!(
            exec_builtin("__str_contains", &[s("Hello"), s("world")]).unwrap(),
            Value::Bool(false)
        );
    }

    // ── __str_starts_with ─────────────────────────────────────────────────────

    #[test]
    fn starts_with_true() {
        assert_eq!(
            exec_builtin("__str_starts_with", &[s("Hello, World!"), s("Hello")]).unwrap(),
            Value::Bool(true)
        );
    }

    #[test]
    fn starts_with_false() {
        assert_eq!(
            exec_builtin("__str_starts_with", &[s("Hello"), s("World")]).unwrap(),
            Value::Bool(false)
        );
    }

    // ── __str_ends_with ───────────────────────────────────────────────────────

    #[test]
    fn ends_with_true() {
        assert_eq!(
            exec_builtin("__str_ends_with", &[s("Hello, World!"), s("!")]).unwrap(),
            Value::Bool(true)
        );
    }

    #[test]
    fn ends_with_false() {
        assert_eq!(
            exec_builtin("__str_ends_with", &[s("Hello"), s("World")]).unwrap(),
            Value::Bool(false)
        );
    }

    // ── dispatch table coverage ───────────────────────────────────────────────

    #[test]
    fn unknown_builtin_errors() {
        assert!(exec_builtin("__nonexistent", &[]).is_err());
    }

    #[test]
    fn println_via_dispatch_table() {
        // Just verify dispatch reaches the function without panicking
        assert!(exec_builtin("__println", &[s("test")]).is_ok());
    }

    #[test]
    fn assert_eq_success() {
        assert!(exec_builtin("__assert_eq", &[i64(42), i64(42)]).is_ok());
    }

    #[test]
    fn assert_eq_failure() {
        assert!(exec_builtin("__assert_eq", &[i64(1), i64(2)]).is_err());
    }
}
