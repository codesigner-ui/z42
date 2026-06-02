//! Unit tests for `native::ext` — search path resolution + lib name parsing.

use super::ext::*;
use std::path::Path;

#[test]
fn parse_z42_lib_name_strips_lib_prefix_and_ext() {
    assert_eq!(parse_z42_lib_name(Path::new("libz42_compression.dylib")).as_deref(),
               Some("compression"));
    assert_eq!(parse_z42_lib_name(Path::new("libz42_compression.so")).as_deref(),
               Some("compression"));
    assert_eq!(parse_z42_lib_name(Path::new("z42_compression.dll")).as_deref(),
               Some("compression"));
    assert_eq!(parse_z42_lib_name(Path::new("/abs/path/libz42_net.so")).as_deref(),
               Some("net"));
}

#[test]
fn parse_z42_lib_name_ignores_non_z42_prefix() {
    assert!(parse_z42_lib_name(Path::new("libcurl.dylib")).is_none());
    assert!(parse_z42_lib_name(Path::new("libsomething_else.so")).is_none());
}

#[test]
fn parse_z42_lib_name_ignores_unknown_extension() {
    assert!(parse_z42_lib_name(Path::new("libz42_compression.txt")).is_none());
    assert!(parse_z42_lib_name(Path::new("libz42_compression.a")).is_none());
    // .a is a staticlib, not for dlopen — correctly skipped.
}

#[test]
fn native_search_paths_includes_default_sdk_layout() {
    // Always returns *something* because we always probe alongside the
    // current_exe; even if the env var is unset, the SDK layout fallbacks
    // are appended.
    let paths = native_search_paths();
    assert!(!paths.is_empty(), "expected at least one search path");
}

#[test]
fn native_search_paths_includes_exec_dir_for_cargo_target_layout() {
    // 2026-05-24 dev-infra: <exec_dir> itself is now a search path so cargo
    // `target/<profile>/libz42_*.dylib` is discovered without manual
    // `ln -sf ../libz42_compression.dylib release/native/libz42_compression.dylib`.
    let exe = std::env::current_exe().expect("current_exe");
    let exec_dir = exe.parent().expect("exec dir").to_path_buf();
    let paths = native_search_paths();
    assert!(
        paths.iter().any(|p| p == &exec_dir),
        "expected <exec_dir> ({}) among search paths, got {:?}",
        exec_dir.display(),
        paths,
    );
}

/// runtime-config-phase2 (2026-06-03): `Z42_NATIVE_PATH` parsing moved
/// to `crate::config::parse_native_search_paths`, covered by
/// `config::tests::from_getter_native_path_splits_on_platform_separator`.
/// `native_search_paths()` now concatenates the parsed list with SDK-
/// relative fallbacks; smoke-test that it returns a non-empty list
/// containing the running binary's exec_dir (the fallback) so the
/// delegator + fallback wiring still works end-to-end.
#[test]
fn native_search_paths_includes_exec_dir_fallback() {
    let paths = native_search_paths();
    let exec_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .expect("test binary must have an exec_dir");
    assert!(
        paths.iter().any(|p| p == &exec_dir),
        "expected exec_dir fallback ({}) among search paths, got {paths:?}",
        exec_dir.display(),
    );
}

#[test]
fn ext_builtin_table_register_and_lookup() {
    use crate::corelib::NativeFn;
    use crate::metadata::Value;
    use crate::vm_context::VmContext;
    use anyhow::Result;

    fn dummy_fn(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(Value::I64(42))
    }
    let f: NativeFn = dummy_fn;

    let mut table = ExtBuiltinTable::default();
    let id0 = table.register("__test_foo", f);
    let id1 = table.register("__test_bar", f);
    assert_ne!(id0, id1);

    assert_eq!(table.lookup_id("__test_foo"), Some(id0));
    assert_eq!(table.lookup_id("__test_bar"), Some(id1));
    assert_eq!(table.lookup_id("__nonexistent"), None);

    assert!(table.dispatch(id0).is_some());
    assert!(table.dispatch(id1).is_some());
    assert!(table.dispatch(99).is_none());

    // Idempotent: re-register same name returns existing id, doesn't swap.
    let id0_again = table.register("__test_foo", f);
    assert_eq!(id0_again, id0);
}
