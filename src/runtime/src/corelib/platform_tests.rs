//! `Std.Platform` builtin unit tests.

use super::*;
use crate::metadata::Value;
use crate::vm_context::VmContext;

#[test]
fn os_returns_consts_value() {
    let ctx = VmContext::new();
    let Value::Str(os) = builtin_platform_os(&ctx, &[]).unwrap()
        else { panic!("expected Str"); };
    assert_eq!(os, std::env::consts::OS);
    assert!(!os.is_empty(), "os string must not be empty");
}

#[test]
fn arch_returns_consts_value() {
    let ctx = VmContext::new();
    let Value::Str(arch) = builtin_platform_arch(&ctx, &[]).unwrap()
        else { panic!("expected Str"); };
    assert_eq!(arch, std::env::consts::ARCH);
    assert!(!arch.is_empty());
}

#[test]
fn family_returns_consts_value() {
    let ctx = VmContext::new();
    let Value::Str(family) = builtin_platform_family(&ctx, &[]).unwrap()
        else { panic!("expected Str"); };
    assert_eq!(family, std::env::consts::FAMILY);
}

#[test]
fn os_kind_matches_current_os() {
    let ctx = VmContext::new();
    let Value::I64(kind) = builtin_platform_os_kind(&ctx, &[]).unwrap()
        else { panic!("expected I64"); };
    // Verify the value matches the known mapping for this build target.
    #[cfg(target_os = "linux")]
    assert_eq!(kind, 1, "linux → 1");
    #[cfg(target_os = "macos")]
    assert_eq!(kind, 2, "macos → 2");
    #[cfg(target_os = "windows")]
    assert_eq!(kind, 3, "windows → 3");
    #[cfg(target_os = "android")]
    assert_eq!(kind, 4, "android → 4");
    #[cfg(target_os = "ios")]
    assert_eq!(kind, 5, "ios → 5");
    #[cfg(target_arch = "wasm32")]
    assert_eq!(kind, 6, "wasm → 6");
    #[cfg(target_os = "freebsd")]
    assert_eq!(kind, 7, "freebsd → 7");

    // Whatever the host is, kind should be one of the known values.
    assert!((0..=7).contains(&kind), "kind {} out of expected range", kind);
}

#[test]
fn arch_kind_matches_current_arch() {
    let ctx = VmContext::new();
    let Value::I64(kind) = builtin_platform_arch_kind(&ctx, &[]).unwrap()
        else { panic!("expected I64"); };
    #[cfg(target_arch = "x86_64")]
    assert_eq!(kind, 1);
    #[cfg(target_arch = "aarch64")]
    assert_eq!(kind, 2);
    #[cfg(target_arch = "wasm32")]
    assert_eq!(kind, 3);
    #[cfg(target_arch = "x86")]
    assert_eq!(kind, 4);
}
