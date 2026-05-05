use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::{bail, Result};
use std::cell::RefCell;
use super::convert::value_to_str;

// ── R2 完整版 — TestIO sink dispatch ───────────────────────────────────────
//
// thread-local stack of capture buffers. install_*_sink pushes a fresh Vec;
// take_*_buffer pops the top and returns it as a String. While a sink is
// active, builtin_println / builtin_print / builtin_eprintln / builtin_eprint
// route to the top buffer instead of the process stdio.
//
// Stack semantics let nested TestIO.captureStdout calls work intuitively
// (inner capture sees inner output only; outer sees outer pre + post but
// not inner). See spec/archive/2026-05-XX-extend-z42-test-library scenario 5.

thread_local! {
    static STDOUT_SINKS: RefCell<Vec<Vec<u8>>> = const { RefCell::new(Vec::new()) };
    static STDERR_SINKS: RefCell<Vec<Vec<u8>>> = const { RefCell::new(Vec::new()) };
}

fn route_stdout(text: &str, append_newline: bool) {
    STDOUT_SINKS.with(|sinks| {
        let mut s = sinks.borrow_mut();
        if let Some(top) = s.last_mut() {
            top.extend_from_slice(text.as_bytes());
            if append_newline { top.push(b'\n'); }
        } else if append_newline {
            println!("{}", text);
        } else {
            print!("{}", text);
        }
    });
}

fn route_stderr(text: &str, append_newline: bool) {
    STDERR_SINKS.with(|sinks| {
        let mut s = sinks.borrow_mut();
        if let Some(top) = s.last_mut() {
            top.extend_from_slice(text.as_bytes());
            if append_newline { top.push(b'\n'); }
        } else if append_newline {
            eprintln!("{}", text);
        } else {
            eprint!("{}", text);
        }
    });
}

pub fn builtin_println(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let text = args.first().map(value_to_str).unwrap_or_default();
    route_stdout(&text, true);
    Ok(Value::Null)
}

pub fn builtin_print(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let text = args.first().map(value_to_str).unwrap_or_default();
    route_stdout(&text, false);
    Ok(Value::Null)
}

pub fn builtin_eprintln(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let text = args.first().map(value_to_str).unwrap_or_default();
    route_stderr(&text, true);
    Ok(Value::Null)
}

pub fn builtin_eprint(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let text = args.first().map(value_to_str).unwrap_or_default();
    route_stderr(&text, false);
    Ok(Value::Null)
}

pub fn builtin_test_io_install_stdout_sink(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    STDOUT_SINKS.with(|s| s.borrow_mut().push(Vec::new()));
    Ok(Value::Null)
}

pub fn builtin_test_io_take_stdout_buffer(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let bytes = STDOUT_SINKS.with(|s| s.borrow_mut().pop().unwrap_or_default());
    Ok(Value::Str(String::from_utf8_lossy(&bytes).into_owned()))
}

pub fn builtin_test_io_install_stderr_sink(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    STDERR_SINKS.with(|s| s.borrow_mut().push(Vec::new()));
    Ok(Value::Null)
}

pub fn builtin_test_io_take_stderr_buffer(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let bytes = STDERR_SINKS.with(|s| s.borrow_mut().pop().unwrap_or_default());
    Ok(Value::Str(String::from_utf8_lossy(&bytes).into_owned()))
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

#[cfg(test)]
#[path = "io_tests.rs"]
mod io_tests;
