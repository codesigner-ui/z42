use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::Result;
use super::convert::require_str;

// ── File I/O ──────────────────────────────────────────────────────────────────

pub fn builtin_file_read_text(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__file_read_text")?;
    let text = std::fs::read_to_string(path.as_str())?;
    Ok(Value::Str(text))
}
pub fn builtin_file_write_text(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path    = require_str(args, 0, "__file_write_text")?;
    let content = require_str(args, 1, "__file_write_text")?;
    std::fs::write(path.as_str(), content.as_str())?;
    Ok(Value::Null)
}
pub fn builtin_file_append_text(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    use std::io::Write;
    let path    = require_str(args, 0, "__file_append_text")?;
    let content = require_str(args, 1, "__file_append_text")?;
    let mut file = std::fs::OpenOptions::new().append(true).create(true).open(path.as_str())?;
    file.write_all(content.as_bytes())?;
    Ok(Value::Null)
}
pub fn builtin_file_exists(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__file_exists")?;
    Ok(Value::Bool(std::path::Path::new(path.as_str()).exists()))
}
pub fn builtin_file_delete(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__file_delete")?;
    std::fs::remove_file(path.as_str())?;
    Ok(Value::Null)
}
pub fn builtin_file_copy(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let src = require_str(args, 0, "__file_copy")?;
    let dst = require_str(args, 1, "__file_copy")?;
    std::fs::copy(src.as_str(), dst.as_str())?;
    Ok(Value::Null)
}
pub fn builtin_file_move(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let src = require_str(args, 0, "__file_move")?;
    let dst = require_str(args, 1, "__file_move")?;
    std::fs::rename(src.as_str(), dst.as_str())?;
    Ok(Value::Null)
}

// 2026-04-27 wave1-path-script: 5 builtin_path_* removed.
// `Std.IO.Path` 现在是 z42 脚本（Unix `/` 语义），见
// src/libraries/z42.io/src/Path.z42。

// ── Directory ─────────────────────────────────────────────────────────────────
//
// add-std-io-directory (2026-05-13)：Std.IO.Directory 模块。语义遵循 BCL
// `System.IO.Directory`：
//   - Create 等价 `mkdir -p`（递归建中间目录，已存在不报错）
//   - Delete recursive=true → 类似 `rm -rf`
//   - Enumerate 仅直接子项 basename（含文件 + 子目录）
//   - EnumerateRecursive 深度优先全展开，路径相对 root

pub fn builtin_dir_exists(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__dir_exists")?;
    Ok(Value::Bool(std::path::Path::new(path.as_str()).is_dir()))
}

pub fn builtin_dir_create(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__dir_create")?;
    std::fs::create_dir_all(path.as_str())?;
    Ok(Value::Null)
}

pub fn builtin_dir_delete(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__dir_delete")?;
    let recursive = matches!(args.get(1), Some(Value::Bool(true)));
    if recursive {
        std::fs::remove_dir_all(path.as_str())?;
    } else {
        std::fs::remove_dir(path.as_str())?;
    }
    Ok(Value::Null)
}

pub fn builtin_dir_enumerate(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__dir_enumerate")?;
    let mut names: Vec<String> = Vec::new();
    for entry in std::fs::read_dir(path.as_str())? {
        let e = entry?;
        if let Some(name) = e.file_name().to_str() {
            names.push(name.to_string());
        }
    }
    names.sort();
    let list: Vec<Value> = names.into_iter().map(Value::Str).collect();
    Ok(ctx.heap().alloc_array(list))
}

pub fn builtin_dir_enumerate_recursive(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let root = require_str(args, 0, "__dir_enumerate_recursive")?;
    let root_path = std::path::Path::new(root.as_str()).to_path_buf();
    let mut out: Vec<String> = Vec::new();
    walk_dir(&root_path, &root_path, &mut out)?;
    out.sort();
    let list: Vec<Value> = out.into_iter().map(Value::Str).collect();
    Ok(ctx.heap().alloc_array(list))
}

fn walk_dir(
    root: &std::path::Path,
    cur: &std::path::Path,
    out: &mut Vec<String>,
) -> Result<()> {
    for entry in std::fs::read_dir(cur)? {
        let e = entry?;
        let p = e.path();
        let rel = p.strip_prefix(root).unwrap_or(&p);
        if let Some(s) = rel.to_str() {
            out.push(s.to_string());
        }
        if e.file_type()?.is_dir() {
            walk_dir(root, &p, out)?;
        }
    }
    Ok(())
}

// ── Environment / Process ─────────────────────────────────────────────────────

pub fn builtin_env_set(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let name  = require_str(args, 0, "__env_set")?;
    let value = require_str(args, 1, "__env_set")?;
    // Safety: z42 is single-threaded; no concurrent env reads during this call.
    unsafe { std::env::set_var(name.as_str(), value.as_str()); }
    Ok(Value::Null)
}
pub fn builtin_env_get(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let key = require_str(args, 0, "__env_get")?;
    Ok(match std::env::var(key.as_str()) {
        Ok(v)  => Value::Str(v),
        Err(_) => Value::Null,
    })
}
pub fn builtin_env_args(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let list: Vec<Value> = std::env::args().map(Value::Str).collect();
    Ok(ctx.heap().alloc_array(list))
}
pub fn builtin_process_exit(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let code = match args.first() {
        Some(Value::I64(n)) => *n as i32,
        _ => 0,
    };
    std::process::exit(code);
}
pub fn builtin_env_unset(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let key = require_str(args, 0, "__env_unset")?;
    std::env::remove_var(key.as_str());
    Ok(Value::Null)
}
pub fn builtin_env_vars(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let list: Vec<Value> = std::env::vars()
        .map(|(k, v)| Value::Str(format!("{k}={v}")))
        .collect();
    Ok(ctx.heap().alloc_array(list))
}
pub fn builtin_time_now_ms(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    use std::time::{SystemTime, UNIX_EPOCH};
    let ms = SystemTime::now().duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64).unwrap_or(0);
    Ok(Value::I64(ms))
}

// ── Glob + Temp（extend-z42-io-glob-temp, 2026-05-16）─────────────────────────
//
// Phase 0b of script self-hosting：试点脚本所需的最小补丁集。

/// Glob `dir/pattern` 直接子项；pattern 支持 `*`（任意序列）和 `?`（单字符）。
/// 大小写敏感；返回 sorted 全路径数组。pattern 不含 `/`。
pub fn builtin_path_glob(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let dir     = require_str(args, 0, "__path_glob")?;
    let pattern = require_str(args, 1, "__path_glob")?;
    let mut hits: Vec<String> = Vec::new();
    if !std::path::Path::new(dir.as_str()).is_dir() {
        return Ok(ctx.heap().alloc_array(Vec::new()));
    }
    for entry in std::fs::read_dir(dir.as_str())? {
        let e = entry?;
        if let Some(name) = e.file_name().to_str() {
            if glob_match(pattern.as_str(), name) {
                // Join dir + '/' + name without re-allocating the dir twice.
                let mut full = String::with_capacity(dir.len() + name.len() + 1);
                full.push_str(dir.as_str());
                if !dir.as_str().ends_with('/') { full.push('/'); }
                full.push_str(name);
                hits.push(full);
            }
        }
    }
    hits.sort();
    let list: Vec<Value> = hits.into_iter().map(Value::Str).collect();
    Ok(ctx.heap().alloc_array(list))
}

/// `*` / `?` glob matcher — recursion-free, backtracking via two cursors
/// (same idea as the classic K&R wildcard match).
fn glob_match(pattern: &str, text: &str) -> bool {
    let p: Vec<char> = pattern.chars().collect();
    let t: Vec<char> = text.chars().collect();
    let mut pi = 0usize;
    let mut ti = 0usize;
    let mut star_pi: Option<usize> = None;
    let mut star_ti = 0usize;
    while ti < t.len() {
        if pi < p.len() && (p[pi] == '?' || p[pi] == t[ti]) {
            pi += 1;
            ti += 1;
        } else if pi < p.len() && p[pi] == '*' {
            star_pi = Some(pi);
            star_ti = ti;
            pi += 1;
        } else if let Some(sp) = star_pi {
            pi = sp + 1;
            star_ti += 1;
            ti = star_ti;
        } else {
            return false;
        }
    }
    while pi < p.len() && p[pi] == '*' { pi += 1; }
    pi == p.len()
}

/// 创建唯一临时目录在 system temp root，返回全路径。
/// 命名：`prefix.<nanos>.<pid>`（极低冲突概率 + 单进程内可重复调用）。
pub fn builtin_file_create_temp_dir(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let prefix = require_str(args, 0, "__file_create_temp_dir")?;
    let path   = unique_temp_path(prefix.as_str(), "");
    std::fs::create_dir_all(&path)?;
    Ok(Value::Str(path))
}

/// 创建唯一临时文件（touched，0 bytes）在 system temp root，返回全路径。
/// 命名：`prefix.<nanos>.<pid><suffix>`。suffix 可为 ""（无后缀）。
pub fn builtin_file_create_temp_file(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let prefix = require_str(args, 0, "__file_create_temp_file")?;
    let suffix = require_str(args, 1, "__file_create_temp_file")?;
    let path   = unique_temp_path(prefix.as_str(), suffix.as_str());
    std::fs::OpenOptions::new().write(true).create(true).truncate(true).open(&path)?;
    Ok(Value::Str(path))
}

fn unique_temp_path(prefix: &str, suffix: &str) -> String {
    use std::time::{SystemTime, UNIX_EPOCH};
    use std::sync::atomic::{AtomicU64, Ordering};
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64).unwrap_or(0);
    let pid   = std::process::id();
    let bump  = COUNTER.fetch_add(1, Ordering::Relaxed);
    let mut p = std::env::temp_dir();
    p.push(format!("{prefix}.{nanos:x}.{pid}.{bump:x}{suffix}"));
    p.to_string_lossy().into_owned()
}
