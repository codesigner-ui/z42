/// Builtin function dispatch — called by the interpreter via `Builtin` instructions.

use crate::types::Value;
use anyhow::{bail, Result};
use super::helpers::{require_str, require_usize, value_to_str};

pub fn exec_builtin(name: &str, args: &[Value]) -> Result<Value> {
    match name {
        // ── I/O ──────────────────────────────────────────────────────────────
        "__println" => {
            let text = args.first().map(value_to_str).unwrap_or_default();
            println!("{}", text);
            Ok(Value::Null)
        }
        "__print" => {
            let text = args.first().map(value_to_str).unwrap_or_default();
            print!("{}", text);
            Ok(Value::Null)
        }
        "__concat" => {
            let a = args.first().map(value_to_str).unwrap_or_default();
            let b = args.get(1).map(value_to_str).unwrap_or_default();
            Ok(Value::Str(format!("{}{}", a, b)))
        }

        // ── Length (works on both arrays and strings) ─────────────────────
        "__len" => match args.first() {
            Some(Value::Array(rc)) => Ok(Value::I64(rc.borrow().len() as i64)),
            Some(Value::Str(s))    => Ok(Value::I64(s.len() as i64)),  // UTF-8 byte count
            Some(other)            => bail!("__len: expected array or string, got {:?}", other),
            None                   => bail!("__len: missing argument"),
        },

        // ── String built-ins ─────────────────────────────────────────────────
        "__str_substring" => {
            let s     = require_str(args, 0, "__str_substring")?;
            let start = require_usize(args, 1, "__str_substring")?;
            if args.len() == 2 {
                if start > s.len() {
                    bail!("__str_substring: start {} out of range (len={})", start, s.len());
                }
                Ok(Value::Str(s[start..].to_string()))
            } else {
                let len = require_usize(args, 2, "__str_substring")?;
                let end = start + len;
                if end > s.len() {
                    bail!("__str_substring: range {}..{} out of range (len={})", start, end, s.len());
                }
                Ok(Value::Str(s[start..end].to_string()))
            }
        }

        "__str_contains" => {
            let s   = require_str(args, 0, "__str_contains")?;
            let sub = require_str(args, 1, "__str_contains")?;
            Ok(Value::Bool(s.contains(sub.as_str())))
        }

        "__str_starts_with" => {
            let s      = require_str(args, 0, "__str_starts_with")?;
            let prefix = require_str(args, 1, "__str_starts_with")?;
            Ok(Value::Bool(s.starts_with(prefix.as_str())))
        }

        "__str_ends_with" => {
            let s      = require_str(args, 0, "__str_ends_with")?;
            let suffix = require_str(args, 1, "__str_ends_with")?;
            Ok(Value::Bool(s.ends_with(suffix.as_str())))
        }

        // ── Assert built-ins ─────────────────────────────────────────────────
        "__assert_eq" => {
            let expected = args.first().cloned().unwrap_or(Value::Null);
            let actual   = args.get(1).cloned().unwrap_or(Value::Null);
            if expected != actual {
                bail!("AssertionError: expected {} but got {}",
                    value_to_str(&expected), value_to_str(&actual));
            }
            Ok(Value::Null)
        }

        "__assert_true" => {
            match args.first() {
                Some(Value::Bool(true)) => Ok(Value::Null),
                Some(other) => bail!("AssertionError: expected true but got {}", value_to_str(other)),
                None        => bail!("AssertionError: __assert_true missing argument"),
            }
        }

        "__assert_false" => {
            match args.first() {
                Some(Value::Bool(false)) => Ok(Value::Null),
                Some(other) => bail!("AssertionError: expected false but got {}", value_to_str(other)),
                None        => bail!("AssertionError: __assert_false missing argument"),
            }
        }

        "__assert_contains" => {
            let sub = require_str(args, 0, "__assert_contains")?;
            let s   = require_str(args, 1, "__assert_contains")?;
            if !s.contains(sub.as_str()) {
                bail!("AssertionError: expected \"{}\" to contain \"{}\"", s, sub);
            }
            Ok(Value::Null)
        }

        "__assert_null" => {
            match args.first() {
                Some(Value::Null) | None => Ok(Value::Null),
                Some(other) => bail!("AssertionError: expected null but got {}", value_to_str(other)),
            }
        }

        "__assert_not_null" => {
            match args.first() {
                Some(Value::Null) | None => bail!("AssertionError: expected non-null but got null"),
                Some(_) => Ok(Value::Null),
            }
        }

        // ── More string built-ins ─────────────────────────────────────────────

        "__str_index_of" => {
            let s   = require_str(args, 0, "__str_index_of")?;
            let sub = require_str(args, 1, "__str_index_of")?;
            let idx = s.find(sub.as_str()).map(|i| i as i64).unwrap_or(-1);
            Ok(Value::I64(idx))
        }

        "__str_replace" => {
            let s    = require_str(args, 0, "__str_replace")?;
            let from = require_str(args, 1, "__str_replace")?;
            let to   = require_str(args, 2, "__str_replace")?;
            Ok(Value::Str(s.replace(from.as_str(), to.as_str())))
        }

        "__str_to_lower" => {
            let s = require_str(args, 0, "__str_to_lower")?;
            Ok(Value::Str(s.to_lowercase()))
        }

        "__str_to_upper" => {
            let s = require_str(args, 0, "__str_to_upper")?;
            Ok(Value::Str(s.to_uppercase()))
        }

        "__str_trim" => {
            let s = require_str(args, 0, "__str_trim")?;
            Ok(Value::Str(s.trim().to_string()))
        }

        "__str_trim_start" => {
            let s = require_str(args, 0, "__str_trim_start")?;
            Ok(Value::Str(s.trim_start().to_string()))
        }

        "__str_trim_end" => {
            let s = require_str(args, 0, "__str_trim_end")?;
            Ok(Value::Str(s.trim_end().to_string()))
        }

        // Unified contains: works for both string and Array (List<T>)
        "__contains" => match args.first() {
            Some(Value::Str(s)) => {
                let needle = require_str(args, 1, "__contains")?;
                Ok(Value::Bool(s.contains(needle.as_str())))
            }
            Some(Value::Array(arr)) => {
                let item = args.get(1).cloned().unwrap_or(Value::Null);
                Ok(Value::Bool(arr.borrow().iter().any(|v| v == &item)))
            }
            _ => bail!("Contains: first argument must be a string or List"),
        },

        "__str_split" => {
            let s   = require_str(args, 0, "__str_split")?;
            let sep = require_str(args, 1, "__str_split")?;
            let parts: Vec<Value> = s.split(sep.as_str())
                .map(|p| Value::Str(p.to_string()))
                .collect();
            Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(parts))))
        }

        // ── Math built-ins ────────────────────────────────────────────────────

        "__math_abs" => match args.first() {
            Some(Value::I64(n)) => Ok(Value::I64(n.abs())),
            Some(Value::F64(f)) => Ok(Value::F64(f.abs())),
            Some(other) => bail!("Math.Abs: unsupported type {:?}", other),
            None => bail!("Math.Abs: missing argument"),
        },

        "__math_max" => match (args.first(), args.get(1)) {
            (Some(Value::I64(a)), Some(Value::I64(b))) => Ok(Value::I64(*a.max(b))),
            (Some(Value::F64(a)), Some(Value::F64(b))) => Ok(Value::F64(a.max(*b))),
            _ => bail!("Math.Max: expected two numeric arguments"),
        },

        "__math_min" => match (args.first(), args.get(1)) {
            (Some(Value::I64(a)), Some(Value::I64(b))) => Ok(Value::I64(*a.min(b))),
            (Some(Value::F64(a)), Some(Value::F64(b))) => Ok(Value::F64(a.min(*b))),
            _ => bail!("Math.Min: expected two numeric arguments"),
        },

        "__math_pow" => match (args.first(), args.get(1)) {
            (Some(Value::I64(base)), Some(Value::I64(exp))) =>
                Ok(Value::I64(base.pow(*exp as u32))),
            (Some(Value::F64(base)), Some(Value::F64(exp))) =>
                Ok(Value::F64(base.powf(*exp))),
            _ => bail!("Math.Pow: expected two numeric arguments"),
        },

        "__math_sqrt" => match args.first() {
            Some(Value::F64(f)) => Ok(Value::F64(f.sqrt())),
            Some(Value::I64(n)) => Ok(Value::F64((*n as f64).sqrt())),
            Some(other) => bail!("Math.Sqrt: unsupported type {:?}", other),
            None => bail!("Math.Sqrt: missing argument"),
        },

        "__math_floor" => match args.first() {
            Some(Value::F64(f)) => Ok(Value::F64(f.floor())),
            Some(Value::I64(n)) => Ok(Value::I64(*n)),
            Some(other) => bail!("Math.Floor: unsupported type {:?}", other),
            None => bail!("Math.Floor: missing argument"),
        },

        "__math_ceiling" => match args.first() {
            Some(Value::F64(f)) => Ok(Value::F64(f.ceil())),
            Some(Value::I64(n)) => Ok(Value::I64(*n)),
            Some(other) => bail!("Math.Ceiling: unsupported type {:?}", other),
            None => bail!("Math.Ceiling: missing argument"),
        },

        "__math_round" => match args.first() {
            Some(Value::F64(f)) => Ok(Value::F64(f.round())),
            Some(Value::I64(n)) => Ok(Value::I64(*n)),
            Some(other) => bail!("Math.Round: unsupported type {:?}", other),
            None => bail!("Math.Round: missing argument"),
        },

        // ── List built-ins ────────────────────────────────────────────────────

        "__list_new" => {
            Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(vec![]))))
        }

        "__list_add" => {
            match args.first() {
                Some(Value::Array(arr)) => {
                    let item = args.get(1).cloned().unwrap_or(Value::Null);
                    arr.borrow_mut().push(item);
                    Ok(Value::Null)
                }
                _ => bail!("List.Add: first argument must be a List"),
            }
        }

        "__list_remove_at" => {
            match (args.first(), args.get(1)) {
                (Some(Value::Array(arr)), Some(Value::I64(idx))) => {
                    let idx = *idx as usize;
                    let mut v = arr.borrow_mut();
                    if idx >= v.len() {
                        bail!("List.RemoveAt: index {} out of range (len={})", idx, v.len());
                    }
                    v.remove(idx);
                    Ok(Value::Null)
                }
                _ => bail!("List.RemoveAt: expected (List, i64)"),
            }
        }

        "__list_contains" => {
            match args.first() {
                Some(Value::Array(arr)) => {
                    let item = args.get(1).cloned().unwrap_or(Value::Null);
                    let found = arr.borrow().iter().any(|v| v == &item);
                    Ok(Value::Bool(found))
                }
                _ => bail!("List.Contains: first argument must be a List"),
            }
        }

        "__list_clear" => {
            match args.first() {
                Some(Value::Array(arr)) => {
                    arr.borrow_mut().clear();
                    Ok(Value::Null)
                }
                _ => bail!("List.Clear: first argument must be a List"),
            }
        }

        "__list_insert" => {
            match (args.first(), args.get(1), args.get(2)) {
                (Some(Value::Array(arr)), Some(Value::I64(idx)), Some(item)) => {
                    let idx = *idx as usize;
                    let item = item.clone();
                    let mut v = arr.borrow_mut();
                    if idx > v.len() {
                        bail!("List.Insert: index {} out of range (len={})", idx, v.len());
                    }
                    v.insert(idx, item);
                    Ok(Value::Null)
                }
                _ => bail!("List.Insert: expected (List, i64, value)"),
            }
        }

        other => bail!("unknown builtin `{other}`"),
    }
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

    // ── unknown builtin ───────────────────────────────────────────────────────

    #[test]
    fn unknown_builtin_errors() {
        assert!(exec_builtin("__nonexistent", &[]).is_err());
    }
}
