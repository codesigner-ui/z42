//! `Std.Platform` builtins — OS / architecture / family identity.
//!
//! All values pass through from `std::env::consts::{OS, ARCH, FAMILY}` which
//! are compile-time constants from rustc's target triple. The Kind value
//! mapping below must stay in lockstep with
//! `src/libraries/z42.io/src/Platform.z42` constants `OSKind::*` /
//! `ArchKind::*` (the z42 stdlib spec lists the canonical values).
//!
//! 2026-05-14 add-platform-os-stdlib.

use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::Result;

pub fn builtin_platform_os(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(std::env::consts::OS.to_string().into()))
}

pub fn builtin_platform_arch(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(std::env::consts::ARCH.to_string().into()))
}

pub fn builtin_platform_family(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(std::env::consts::FAMILY.to_string().into()))
}

/// Keep in sync with `Std.OSKind` in
/// `src/libraries/z42.core/src/Platform.z42`.
pub fn builtin_platform_os_kind(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let kind: i64 = match std::env::consts::OS {
        "linux"   => 1,
        "macos"   => 2,
        "windows" => 3,
        "android" => 4,
        "ios"     => 5,
        "wasm"    => 6,
        "freebsd" => 7,
        _         => 0,
    };
    Ok(Value::I64(kind))
}

/// Keep in sync with `Std.ArchKind` in
/// `src/libraries/z42.core/src/Platform.z42`. Names use the .NET-style
/// short forms (X64 / Arm64 / Wasm / X86); the integer values are what
/// matters at the ABI boundary.
pub fn builtin_platform_arch_kind(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let kind: i64 = match std::env::consts::ARCH {
        "x86_64"  => 1,
        "aarch64" => 2,
        "wasm32"  => 3,
        "x86"     => 4,
        _         => 0,
    };
    Ok(Value::I64(kind))
}

#[cfg(test)]
#[path = "platform_tests.rs"]
mod platform_tests;
