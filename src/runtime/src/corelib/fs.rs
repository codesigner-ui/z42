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

// ── Path ──────────────────────────────────────────────────────────────────────

pub fn builtin_path_join(args: &[Value]) -> Result<Value> {
    let a = require_str(args, 0, "__path_join")?;
    let b = require_str(args, 1, "__path_join")?;
    Ok(Value::Str(std::path::Path::new(a.as_str()).join(b.as_str()).to_string_lossy().into_owned()))
}
pub fn builtin_path_get_extension(args: &[Value]) -> Result<Value> {
    let p = require_str(args, 0, "__path_get_extension")?;
    Ok(Value::Str(std::path::Path::new(p.as_str()).extension()
        .and_then(|e| e.to_str()).unwrap_or("").to_string()))
}
pub fn builtin_path_get_filename(args: &[Value]) -> Result<Value> {
    let p = require_str(args, 0, "__path_get_filename")?;
    Ok(Value::Str(std::path::Path::new(p.as_str()).file_name()
        .and_then(|n| n.to_str()).unwrap_or("").to_string()))
}
pub fn builtin_path_get_directory(args: &[Value]) -> Result<Value> {
    let p = require_str(args, 0, "__path_get_directory")?;
    Ok(Value::Str(std::path::Path::new(p.as_str()).parent()
        .and_then(|d| d.to_str()).unwrap_or("").to_string()))
}
pub fn builtin_path_get_filename_without_ext(args: &[Value]) -> Result<Value> {
    let p = require_str(args, 0, "__path_get_filename_without_ext")?;
    Ok(Value::Str(std::path::Path::new(p.as_str()).file_stem()
        .and_then(|n| n.to_str()).unwrap_or("").to_string()))
}

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
        Some(Value::I32(n)) => *n,
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
