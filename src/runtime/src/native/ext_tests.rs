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
fn native_search_paths_respects_env_var() {
    use std::sync::Mutex;
    // serialise so concurrent tests don't trample each other's env var
    static ENV_LOCK: Mutex<()> = Mutex::new(());
    let _g = ENV_LOCK.lock().unwrap();

    let prev = std::env::var("Z42_NATIVE_PATH").ok();
    let sep = if cfg!(windows) { ";" } else { ":" };
    std::env::set_var("Z42_NATIVE_PATH", format!("/tmp/a{sep}/tmp/b"));

    let paths = native_search_paths();
    let strs: Vec<String> = paths.iter()
        .map(|p| p.to_string_lossy().into_owned())
        .collect();
    assert!(strs.iter().any(|s| s == "/tmp/a"), "missing /tmp/a, got {strs:?}");
    assert!(strs.iter().any(|s| s == "/tmp/b"), "missing /tmp/b, got {strs:?}");

    // restore
    match prev {
        Some(v) => std::env::set_var("Z42_NATIVE_PATH", v),
        None    => std::env::remove_var("Z42_NATIVE_PATH"),
    }
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
