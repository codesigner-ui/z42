use crate::metadata::Value;
use anyhow::Result;
use super::convert::require_str;

// ── File I/O ──────────────────────────────────────────────────────────────────

pub fn builtin_file_read_text(args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__file_read_text")?;
    let text = std::fs::read_to_string(path.as_str())?;
    Ok(Value::Str(text))
}
pub fn builtin_file_write_text(args: &[Value]) -> Result<Value> {
    let path    = require_str(args, 0, "__file_write_text")?;
    let content = require_str(args, 1, "__file_write_text")?;
    std::fs::write(path.as_str(), content.as_str())?;
    Ok(Value::Null)
}
pub fn builtin_file_append_text(args: &[Value]) -> Result<Value> {
    use std::io::Write;
    let path    = require_str(args, 0, "__file_append_text")?;
    let content = require_str(args, 1, "__file_append_text")?;
    let mut file = std::fs::OpenOptions::new().append(true).create(true).open(path.as_str())?;
    file.write_all(content.as_bytes())?;
    Ok(Value::Null)
}
pub fn builtin_file_exists(args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__file_exists")?;
    Ok(Value::Bool(std::path::Path::new(path.as_str()).exists()))
}
pub fn builtin_file_delete(args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__file_delete")?;
    std::fs::remove_file(path.as_str())?;
    Ok(Value::Null)
}

// 2026-04-27 wave1-path-script: 5 builtin_path_* removed.
// `Std.IO.Path` 现在是 z42 脚本（Unix `/` 语义），见
// src/libraries/z42.io/src/Path.z42。

// ── Environment / Process ─────────────────────────────────────────────────────

pub fn builtin_env_get(args: &[Value]) -> Result<Value> {
    let key = require_str(args, 0, "__env_get")?;
    Ok(match std::env::var(key.as_str()) {
        Ok(v)  => Value::Str(v),
        Err(_) => Value::Null,
    })
}
pub fn builtin_env_args(_args: &[Value]) -> Result<Value> {
    let list: Vec<Value> = std::env::args().map(Value::Str).collect();
    Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(list))))
}
pub fn builtin_process_exit(args: &[Value]) -> Result<Value> {
    let code = match args.first() {
        Some(Value::I64(n)) => *n as i32,
        _ => 0,
    };
    std::process::exit(code);
}
pub fn builtin_time_now_ms(_args: &[Value]) -> Result<Value> {
    let ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0);
    Ok(Value::I64(ms))
}
