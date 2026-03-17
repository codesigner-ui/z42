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
            Some(Value::Array(rc)) => Ok(Value::I32(rc.borrow().len() as i32)),
            Some(Value::Str(s))    => Ok(Value::I32(s.len() as i32)),  // UTF-8 byte count
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

        other => bail!("unknown builtin `{other}`"),
    }
}
