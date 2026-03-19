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
