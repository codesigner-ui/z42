use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::Result;
use super::convert::arg_str;

// ── File I/O ──────────────────────────────────────────────────────────────────

pub fn builtin_file_read_text(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = arg_str(args, 0, "__file_read_text")?;
    let text = std::fs::read_to_string(path)?;
    Ok(Value::Str(text))
}
pub fn builtin_file_write_text(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path    = arg_str(args, 0, "__file_write_text")?;
    let content = arg_str(args, 1, "__file_write_text")?;
    std::fs::write(path, content)?;
    Ok(Value::Null)
}
pub fn builtin_file_append_text(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    use std::io::Write;
    let path    = arg_str(args, 0, "__file_append_text")?;
    let content = arg_str(args, 1, "__file_append_text")?;
    let mut file = std::fs::OpenOptions::new().append(true).create(true).open(path)?;
    file.write_all(content.as_bytes())?;
    Ok(Value::Null)
}
pub fn builtin_file_exists(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = arg_str(args, 0, "__file_exists")?;
    Ok(Value::Bool(std::path::Path::new(path).exists()))
}
pub fn builtin_file_delete(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = arg_str(args, 0, "__file_delete")?;
    std::fs::remove_file(path)?;
    Ok(Value::Null)
}
pub fn builtin_file_copy(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let src = arg_str(args, 0, "__file_copy")?;
    let dst = arg_str(args, 1, "__file_copy")?;
    std::fs::copy(src, dst)?;
    Ok(Value::Null)
}
pub fn builtin_file_move(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let src = arg_str(args, 0, "__file_move")?;
    let dst = arg_str(args, 1, "__file_move")?;
    std::fs::rename(src, dst)?;
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
    let path = arg_str(args, 0, "__dir_exists")?;
    Ok(Value::Bool(std::path::Path::new(path).is_dir()))
}

pub fn builtin_dir_create(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = arg_str(args, 0, "__dir_create")?;
    std::fs::create_dir_all(path)?;
    Ok(Value::Null)
}

pub fn builtin_dir_delete(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = arg_str(args, 0, "__dir_delete")?;
    let recursive = matches!(args.get(1), Some(Value::Bool(true)));
    if recursive {
        std::fs::remove_dir_all(path)?;
    } else {
        std::fs::remove_dir(path)?;
    }
    Ok(Value::Null)
}

pub fn builtin_dir_enumerate(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = arg_str(args, 0, "__dir_enumerate")?;
    let mut names: Vec<String> = Vec::new();
    for entry in std::fs::read_dir(path)? {
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
    let root = arg_str(args, 0, "__dir_enumerate_recursive")?;
    let root_path = std::path::Path::new(root).to_path_buf();
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
    let name = arg_str(args, 0, "__env_set")?;
    // Null value = remove (mirrors .NET Environment.SetEnvironmentVariable(name, null)).
    match args.get(1).unwrap_or(&Value::Null) {
        Value::Null => unsafe { std::env::remove_var(name) },
        v => {
            let s = match v {
                Value::Str(s) => s.as_str(),
                _ => anyhow::bail!("__env_set: arg 1 expected string or null, got {:?}", v),
            };
            // Safety: z42 is single-threaded; no concurrent env reads during this call.
            unsafe { std::env::set_var(name, s); }
        }
    }
    Ok(Value::Null)
}
pub fn builtin_env_get(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let key = arg_str(args, 0, "__env_get")?;
    Ok(match std::env::var(key) {
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
    let key = arg_str(args, 0, "__env_unset")?;
    std::env::remove_var(key);
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
    let dir     = arg_str(args, 0, "__path_glob")?;
    let pattern = arg_str(args, 1, "__path_glob")?;
    let mut hits: Vec<String> = Vec::new();
    if !std::path::Path::new(dir).is_dir() {
        return Ok(ctx.heap().alloc_array(Vec::new()));
    }
    for entry in std::fs::read_dir(dir)? {
        let e = entry?;
        if let Some(name) = e.file_name().to_str() {
            if glob_match(pattern, name) {
                // Join dir + '/' + name without re-allocating the dir twice.
                let mut full = String::with_capacity(dir.len() + name.len() + 1);
                full.push_str(dir);
                if !dir.ends_with('/') { full.push('/'); }
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
    let prefix = arg_str(args, 0, "__file_create_temp_dir")?;
    let path   = unique_temp_path(prefix, "");
    std::fs::create_dir_all(&path)?;
    Ok(Value::Str(path))
}

/// 创建唯一临时文件（touched，0 bytes）在 system temp root，返回全路径。
/// 命名：`prefix.<nanos>.<pid><suffix>`。suffix 可为 ""（无后缀）。
pub fn builtin_file_create_temp_file(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let prefix = arg_str(args, 0, "__file_create_temp_file")?;
    let suffix = arg_str(args, 1, "__file_create_temp_file")?;
    let path   = unique_temp_path(prefix, suffix);
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

// ── Script helpers（extend-z42-io-script-helpers, 2026-05-16）─────────────────

/// Unix: `chmod u+x,g+x,o+x`（owner / group / world execute）；Windows: no-op
/// （NTFS 文件可执行性由扩展名而非 ACL bit 决定）。
pub fn builtin_file_make_executable(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = arg_str(args, 0, "__file_make_executable")?;
    #[cfg(unix)] {
        use std::os::unix::fs::PermissionsExt;
        let mut perms = std::fs::metadata(path)?.permissions();
        let mode = perms.mode() | 0o111;
        perms.set_mode(mode);
        std::fs::set_permissions(path, perms)?;
    }
    #[cfg(not(unix))] { let _ = path; }
    Ok(Value::Null)
}

/// 创建 hard link（dst → src）。跨设备时 OS 错误透传。
pub fn builtin_file_link(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let src = arg_str(args, 0, "__file_link")?;
    let dst = arg_str(args, 1, "__file_link")?;
    std::fs::hard_link(src, dst)?;
    Ok(Value::Null)
}

/// 创建 symbolic link（dst → src）。Windows 暂未实现（需 privilege）。
pub fn builtin_file_symlink(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let src = arg_str(args, 0, "__file_symlink")?;
    let dst = arg_str(args, 1, "__file_symlink")?;
    #[cfg(unix)] {
        std::os::unix::fs::symlink(src, dst)?;
    }
    #[cfg(not(unix))] {
        anyhow::bail!("File.SymLink: not implemented on this platform");
    }
    Ok(Value::Null)
}

/// 文件字节数（dir 错误）。
pub fn builtin_file_get_size(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = arg_str(args, 0, "__file_get_size")?;
    let meta = std::fs::metadata(path)?;
    if meta.is_dir() {
        anyhow::bail!("File.GetSize: '{}' is a directory", path);
    }
    Ok(Value::I64(meta.len() as i64))
}

/// stdout 是否连接 tty（颜色 / 进度条决策）。
pub fn builtin_console_is_terminal(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    use std::io::IsTerminal;
    Ok(Value::Bool(std::io::stdout().is_terminal()))
}

/// stderr 是否连接 tty。
pub fn builtin_console_error_is_terminal(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    use std::io::IsTerminal;
    Ok(Value::Bool(std::io::stderr().is_terminal()))
}

/// `pwd` / `$PWD`。
pub fn builtin_env_get_cwd(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let p = std::env::current_dir()?;
    Ok(Value::Str(p.to_string_lossy().into_owned()))
}

/// `cd path`（路径不存在 / 无权限会抛）。
pub fn builtin_env_set_cwd(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = arg_str(args, 0, "__env_set_cwd")?;
    std::env::set_current_dir(path)?;
    Ok(Value::Null)
}

// ── add-z42-io-filestream (2026-05-24) — FileStream slot table + 8 ops ────
//
// Mirrors the slot-table pattern used by `processes` / `mutexes` /
// `channels` / `compressors`: each opened OS handle lands in
// `VmCore.file_handles` keyed by a monotonic u64 id; z42-side
// `Std.IO.FileStream` holds the id; all operations look up the slot,
// perform the syscall via `std::fs::File`, return the result.
//
// The `Option<File>` slot lets `__file_close` drop the underlying
// handle (releasing the OS fd) while leaving the slot present —
// subsequent operations see `None` and report a clear "file closed"
// error instead of dangling-slot UB.

use std::sync::atomic::Ordering;
use super::convert::arg_i64;

/// 0 = Read (open existing, read-only)
/// 1 = Write (create-or-truncate, write-only)
/// 2 = Append (create-if-missing / append, write-only; O_APPEND on POSIX
///     so writes always go to EOF and `seek` is a no-op on the write
///     side — z42 facade reports CanSeek() == false in this mode)
pub(crate) struct FileHandleSlot {
    pub(crate) file: Option<std::fs::File>,
    #[allow(dead_code)] // reserved for future stricter mode-aware error messages
    pub(crate) mode: i64,
}

fn require_slot_mut<'a>(
    slots: &'a mut std::collections::HashMap<u64, FileHandleSlot>,
    id: u64,
    op: &str,
) -> Result<&'a mut FileHandleSlot> {
    slots.get_mut(&id)
        .ok_or_else(|| anyhow::anyhow!("{}: file handle {} not found", op, id))
}

fn require_open_file<'a>(slot: &'a mut FileHandleSlot, op: &str) -> Result<&'a mut std::fs::File> {
    slot.file.as_mut()
        .ok_or_else(|| anyhow::anyhow!("{}: file handle is closed", op))
}

fn require_byte_array(args: &[Value], idx: usize, op: &str) -> Result<Vec<u8>> {
    match args.get(idx) {
        Some(Value::Array(rc)) => {
            let borrowed = rc.borrow();
            let mut out = Vec::with_capacity(borrowed.len());
            for (i, v) in borrowed.iter().enumerate() {
                match v {
                    Value::I64(n) if (0..=255).contains(n) => out.push(*n as u8),
                    other => anyhow::bail!("{}: arg {} byte {} not u8 in 0..=255: {:?}",
                                           op, idx, i, other),
                }
            }
            Ok(out)
        }
        Some(other) => anyhow::bail!("{}: arg {} expected byte array, got {:?}", op, idx, other),
        None => anyhow::bail!("{}: missing arg {}", op, idx),
    }
}

/// `__file_open(path: string, mode: int) -> long`
pub fn builtin_file_open(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    const NAME: &str = "__file_open";
    let path = arg_str(args, 0, NAME)?;
    let mode = arg_i64(args, 1, NAME)?;
    let file = match mode {
        0 => std::fs::OpenOptions::new().read(true).open(path)?,
        1 => std::fs::OpenOptions::new().write(true).create(true).truncate(true).open(path)?,
        2 => std::fs::OpenOptions::new().append(true).create(true).open(path)?,
        n => anyhow::bail!("{}: unknown FileMode {} (expected 0=Read, 1=Write, 2=Append)", NAME, n),
    };
    let id = ctx.core.next_file_handle_id.fetch_add(1, Ordering::Relaxed);
    ctx.core.file_handles.lock().insert(id, FileHandleSlot { file: Some(file), mode });
    Ok(Value::I64(id as i64))
}

/// `__file_read(slot: long, buf: byte[], offset: int, count: int) -> int`
/// Writes into `buf[offset..offset+count]`; returns bytes actually read
/// (0 = EOF). buf is a z42 `byte[]` which crosses FFI as `Value::Array<I64>`
/// — to stay consistent with the rest of the corelib we round-trip via
/// a temporary `Vec<u8>` slice.
pub fn builtin_file_read(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    use std::io::Read;
    const NAME: &str = "__file_read";
    let slot_id = arg_i64(args, 0, NAME)? as u64;
    let offset  = arg_i64(args, 2, NAME)? as usize;
    let count   = arg_i64(args, 3, NAME)? as usize;
    // We need to write into the user's byte[] in-place. The cleanest path:
    // read into a temp Vec<u8>, then copy slot bytes back into the
    // Value::Array. Validate the buf shape first (must be Array of u8-as-I64).
    let buf_value = args.get(1).cloned()
        .ok_or_else(|| anyhow::anyhow!("{}: missing arg 1 (buf)", NAME))?;
    let buf_arr = match &buf_value {
        Value::Array(rc) => rc.clone(),
        other => anyhow::bail!("{}: arg 1 expected byte array, got {:?}", NAME, other),
    };
    let buf_len = buf_arr.borrow().len();
    if offset + count > buf_len {
        anyhow::bail!("{}: offset {} + count {} exceeds buf length {}", NAME, offset, count, buf_len);
    }
    let mut tmp = vec![0u8; count];
    let n = {
        let mut slots = ctx.core.file_handles.lock();
        let slot = require_slot_mut(&mut slots, slot_id, NAME)?;
        let f = require_open_file(slot, NAME)?;
        f.read(&mut tmp)?
    };
    // Copy actually-read bytes into the user's array.
    let mut borrowed = buf_arr.borrow_mut();
    for i in 0..n {
        borrowed[offset + i] = Value::I64(tmp[i] as i64);
    }
    Ok(Value::I64(n as i64))
}

/// `__file_write(slot: long, buf: byte[], offset: int, count: int)`
pub fn builtin_file_write(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    use std::io::Write;
    const NAME: &str = "__file_write";
    let slot_id = arg_i64(args, 0, NAME)? as u64;
    let offset  = arg_i64(args, 2, NAME)? as usize;
    let count   = arg_i64(args, 3, NAME)? as usize;
    let mut all_bytes = require_byte_array(args, 1, NAME)?;
    if offset + count > all_bytes.len() {
        anyhow::bail!("{}: offset {} + count {} exceeds buf length {}", NAME, offset, count, all_bytes.len());
    }
    // Slice [offset..offset+count] — write only that portion.
    let _ = all_bytes.drain(0..offset);
    all_bytes.truncate(count);
    let mut slots = ctx.core.file_handles.lock();
    let slot = require_slot_mut(&mut slots, slot_id, NAME)?;
    let f = require_open_file(slot, NAME)?;
    f.write_all(&all_bytes)?;
    Ok(Value::Null)
}

/// `__file_seek(slot: long, offset: long, origin: int) -> long` (new abs pos)
pub fn builtin_file_seek(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    use std::io::{Seek, SeekFrom};
    const NAME: &str = "__file_seek";
    let slot_id = arg_i64(args, 0, NAME)? as u64;
    let offset  = arg_i64(args, 1, NAME)?;
    let origin  = arg_i64(args, 2, NAME)?;
    let from = match origin {
        0 => SeekFrom::Start(offset.max(0) as u64),
        1 => SeekFrom::Current(offset),
        2 => SeekFrom::End(offset),
        n => anyhow::bail!("{}: unknown origin {} (expected 0=Begin 1=Current 2=End)", NAME, n),
    };
    let mut slots = ctx.core.file_handles.lock();
    let slot = require_slot_mut(&mut slots, slot_id, NAME)?;
    let f = require_open_file(slot, NAME)?;
    let new_pos = f.seek(from)?;
    Ok(Value::I64(new_pos as i64))
}

/// `__file_length(slot: long) -> long`
pub fn builtin_file_length(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    const NAME: &str = "__file_length";
    let slot_id = arg_i64(args, 0, NAME)? as u64;
    let mut slots = ctx.core.file_handles.lock();
    let slot = require_slot_mut(&mut slots, slot_id, NAME)?;
    let f = require_open_file(slot, NAME)?;
    let meta = f.metadata()?;
    Ok(Value::I64(meta.len() as i64))
}

/// `__file_position(slot: long) -> long`
pub fn builtin_file_position(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    use std::io::{Seek, SeekFrom};
    const NAME: &str = "__file_position";
    let slot_id = arg_i64(args, 0, NAME)? as u64;
    let mut slots = ctx.core.file_handles.lock();
    let slot = require_slot_mut(&mut slots, slot_id, NAME)?;
    let f = require_open_file(slot, NAME)?;
    // Position = seek by 0 from Current.
    let pos = f.seek(SeekFrom::Current(0))?;
    Ok(Value::I64(pos as i64))
}

/// `__file_flush(slot: long)`
pub fn builtin_file_flush(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    use std::io::Write;
    const NAME: &str = "__file_flush";
    let slot_id = arg_i64(args, 0, NAME)? as u64;
    let mut slots = ctx.core.file_handles.lock();
    let slot = require_slot_mut(&mut slots, slot_id, NAME)?;
    let f = require_open_file(slot, NAME)?;
    f.flush()?;
    Ok(Value::Null)
}

/// `__file_close(slot: long)` — idempotent
pub fn builtin_file_close(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    const NAME: &str = "__file_close";
    let slot_id = arg_i64(args, 0, NAME)? as u64;
    let mut slots = ctx.core.file_handles.lock();
    if let Some(slot) = slots.get_mut(&slot_id) {
        // Drop the File (releases OS fd) but leave the slot present so
        // subsequent reads / writes get a clear "file closed" error
        // rather than "slot not found".
        slot.file.take();
    }
    // Unknown slot id is silently OK — Close is idempotent and a no-op
    // on already-closed handles is the conventional behaviour.
    Ok(Value::Null)
}
