//! `Std.OperatingSystem` builtins — process + machine info.
//!
//! Cross-platform impls now flow through `crate::pal::system` (review.md
//! Part 1 P2 Phase 1, add-pal-system-phase1, 2026-06-03). This file is
//! just the builtin-dispatch layer that wraps PAL calls into VM `Value`s.
//!
//! 2026-05-14 add-platform-os-stdlib (original landing).

use super::convert::arg_str;
use crate::metadata::Value;
use crate::pal;
use crate::vm_context::VmContext;
use anyhow::Result;

pub fn builtin_system_pid(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::I64(std::process::id() as i64))
}

pub fn builtin_system_exe_path(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    match std::env::current_exe() {
        Ok(p)  => Ok(Value::Str(p.to_string_lossy().into_owned().into())),
        Err(_) => Ok(Value::Str(String::new().into())),
    }
}

pub fn builtin_system_cwd(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    match std::env::current_dir() {
        Ok(p)  => Ok(Value::Str(p.to_string_lossy().into_owned().into())),
        Err(_) => Ok(Value::Str(String::new().into())),
    }
}

pub fn builtin_system_set_cwd(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = arg_str(args, 0, "__system_set_cwd")?;
    std::env::set_current_dir(path)?;
    Ok(Value::Null)
}

pub fn builtin_system_hostname(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(pal::system::hostname().unwrap_or_default().into()))
}

pub fn builtin_system_cpu_count(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let n = std::thread::available_parallelism()
        .map(|n| n.get() as i64)
        .unwrap_or(1);
    Ok(Value::I64(n))
}

pub fn builtin_system_os_version(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(pal::system::os_version().into()))
}

// add-pal-system-phase1 (2026-06-03): the unix / wasm / windows-stub
// branches that used to live inline here now sit behind `crate::pal::system::*`.
// Future PAL concerns (fs / signal / thread / mem) follow the same pattern —
// see `docs/design/runtime/pal.md` for the migration plan.

#[cfg(test)]
#[path = "system_tests.rs"]
mod system_tests;
