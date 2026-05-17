//! `Std.OperatingSystem` builtins — process + machine info.
//!
//! Cross-platform implementation:
//! - **Unix**: `libc::gethostname` + `libc::uname` for hostname / OS version.
//! - **Windows**: not yet implemented (CI doesn't run windows yet);
//!   returns `""` so script code keeps working with graceful degrade.
//! - **Wasm**: no syscalls; everything returns `""` or sane defaults.
//!
//! 2026-05-14 add-platform-os-stdlib.

use super::convert::arg_str;
use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::Result;

pub fn builtin_system_pid(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::I64(std::process::id() as i64))
}

pub fn builtin_system_exe_path(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    match std::env::current_exe() {
        Ok(p)  => Ok(Value::Str(p.to_string_lossy().into_owned())),
        Err(_) => Ok(Value::Str(String::new())),
    }
}

pub fn builtin_system_cwd(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    match std::env::current_dir() {
        Ok(p)  => Ok(Value::Str(p.to_string_lossy().into_owned())),
        Err(_) => Ok(Value::Str(String::new())),
    }
}

pub fn builtin_system_set_cwd(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = arg_str(args, 0, "__system_set_cwd")?;
    std::env::set_current_dir(path)?;
    Ok(Value::Null)
}

pub fn builtin_system_hostname(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(get_hostname().unwrap_or_default()))
}

pub fn builtin_system_cpu_count(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let n = std::thread::available_parallelism()
        .map(|n| n.get() as i64)
        .unwrap_or(1);
    Ok(Value::I64(n))
}

pub fn builtin_system_os_version(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(get_os_version()))
}

// ── platform-specific helpers ────────────────────────────────────────────

#[cfg(unix)]
fn get_hostname() -> Option<String> {
    use std::ffi::CStr;
    let mut buf = vec![0u8; 256];
    let rc = unsafe {
        libc::gethostname(buf.as_mut_ptr() as *mut libc::c_char, buf.len())
    };
    if rc != 0 { return None; }
    // Find the terminating NUL — gethostname is C-string output.
    let cstr = unsafe { CStr::from_ptr(buf.as_ptr() as *const libc::c_char) };
    Some(cstr.to_string_lossy().into_owned())
}

#[cfg(not(unix))]
fn get_hostname() -> Option<String> {
    // Windows / wasm: graceful degrade. Real Windows impl via GetComputerNameW
    // can land in a follow-up once a Windows CI runner exists.
    None
}

#[cfg(unix)]
fn get_os_version() -> String {
    let mut utsname: libc::utsname = unsafe { std::mem::zeroed() };
    if unsafe { libc::uname(&mut utsname) } != 0 {
        return String::new();
    }
    fn cstr(arr: &[libc::c_char]) -> String {
        let p = arr.as_ptr();
        let cstr = unsafe { std::ffi::CStr::from_ptr(p) };
        cstr.to_string_lossy().into_owned()
    }
    format!("{} {} {}",
        cstr(&utsname.sysname),
        cstr(&utsname.release),
        cstr(&utsname.version))
}

#[cfg(target_arch = "wasm32")]
fn get_os_version() -> String { String::from("wasm") }

#[cfg(all(not(unix), not(target_arch = "wasm32")))]
fn get_os_version() -> String { String::new() }

#[cfg(test)]
#[path = "system_tests.rs"]
mod system_tests;
