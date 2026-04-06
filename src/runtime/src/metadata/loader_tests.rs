use super::*;
use std::path::Path;

// ── extract_import_namespaces ─────────────────────────────────────────────────

fn ns(imports: &[&str]) -> Vec<String> {
    extract_import_namespaces(&imports.iter().map(|s| s.to_string()).collect::<Vec<_>>())
}

#[test]
fn empty_imports_returns_empty() {
    assert!(ns(&[]).is_empty());
}

#[test]
fn single_import_extracts_two_component_namespace() {
    assert_eq!(ns(&["z42.core.String.Contains"]), vec!["z42.core"]);
}

#[test]
fn multiple_imports_same_namespace_deduplicated() {
    assert_eq!(
        ns(&["z42.core.String.Contains", "z42.core.Assert.Equal"]),
        vec!["z42.core"]
    );
}

#[test]
fn imports_from_different_namespaces_all_returned() {
    let result = ns(&["z42.core.String.Contains", "z42.io.File.ReadText"]);
    assert_eq!(result.len(), 2);
    assert!(result.contains(&"z42.core".to_owned()));
    assert!(result.contains(&"z42.io".to_owned()));
}

#[test]
fn import_with_one_dot_uses_full_name() {
    assert_eq!(ns(&["mylib.Foo"]), vec!["mylib.Foo"]);
}

#[test]
fn import_with_no_dot_uses_full_name() {
    assert_eq!(ns(&["standalone"]), vec!["standalone"]);
}

// ── resolve_namespace ─────────────────────────────────────────────────────────

fn make_fake_zpkg(dir: &Path, filename: &str, namespaces: &[&str]) {
    let content = serde_json::json!({
        "name": "test",
        "version": "0.1.0",
        "kind": "lib",
        "mode": "indexed",
        "namespaces": namespaces,
        "exports": [],
        "dependencies": [],
        "files": [],
        "modules": []
    });
    let path = dir.join(filename);
    std::fs::write(path, content.to_string()).expect("write test zpkg");
}

fn make_fake_zbc(dir: &Path, filename: &str, namespace: &str) {
    use crate::metadata::formats::{ZBC_MAGIC, ZBC_VERSION};
    // Minimal binary zbc: header (16 bytes) + NSPC section
    let ns_bytes = namespace.as_bytes();
    let ns_len = ns_bytes.len() as u16;
    let sec_len = (2 + ns_bytes.len()) as u32;

    let mut data: Vec<u8> = Vec::new();
    // Header: magic[4] + major[2] + minor[2] + flags[2] + reserved[6]
    data.extend_from_slice(&ZBC_MAGIC);
    data.extend_from_slice(&ZBC_VERSION[0].to_le_bytes());
    data.extend_from_slice(&ZBC_VERSION[1].to_le_bytes());
    data.extend_from_slice(&0u16.to_le_bytes()); // flags = 0 (full)
    data.extend_from_slice(&[0u8; 6]);           // reserved
    // NSPC section: tag[4] + len[4] + u16(ns_len) + ns_bytes
    data.extend_from_slice(b"NSPC");
    data.extend_from_slice(&sec_len.to_le_bytes());
    data.extend_from_slice(&ns_len.to_le_bytes());
    data.extend_from_slice(ns_bytes);

    let path = dir.join(filename);
    std::fs::write(path, &data).expect("write test zbc");
}

/// resolve_namespace with empty paths returns Ok(None)
#[test]
fn test_resolve_namespace_empty_paths() {
    let result = resolve_namespace("z42.io", &[], &[]);
    assert!(result.is_ok());
    assert!(result.unwrap().is_none());
}

/// Two zpkg files in the same libs tier providing the same namespace → error
#[test]
fn test_resolve_namespace_ambiguous_same_tier() {
    let tmp = std::env::temp_dir().join(format!("z42_test_{}", std::process::id()));
    std::fs::create_dir_all(&tmp).unwrap();

    make_fake_zpkg(&tmp, "libA.zpkg", &["z42.conflict"]);
    make_fake_zpkg(&tmp, "libB.zpkg", &["z42.conflict"]);

    let result = resolve_namespace("z42.conflict", &[], &[tmp.clone()]);
    std::fs::remove_dir_all(&tmp).ok();

    assert!(result.is_err(), "expected ambiguous namespace error");
    let msg = result.unwrap_err().to_string();
    assert!(msg.contains("AmbiguousNamespaceError"), "error message: {msg}");
    assert!(msg.contains("z42.conflict"), "error message: {msg}");
}

/// A zbc in module_paths and a zpkg in libs_paths both provide the same namespace
/// → zbc (module_paths) wins.
#[test]
fn test_resolve_namespace_cross_tier_override() {
    let tmp = std::env::temp_dir().join(format!("z42_test_ct_{}", std::process::id()));
    let zbc_dir  = tmp.join("modules");
    let zpkg_dir = tmp.join("libs");
    std::fs::create_dir_all(&zbc_dir).unwrap();
    std::fs::create_dir_all(&zpkg_dir).unwrap();

    make_fake_zbc(&zbc_dir, "mymod.zbc", "z42.shared");
    make_fake_zpkg(&zpkg_dir, "mylib.zpkg", &["z42.shared"]);

    let result = resolve_namespace("z42.shared", &[zbc_dir.clone()], &[zpkg_dir.clone()]);
    std::fs::remove_dir_all(&tmp).ok();

    assert!(result.is_ok(), "unexpected error: {:?}", result.err());
    let path = result.unwrap().expect("expected Some(path)");
    assert_eq!(
        path.parent().unwrap(),
        zbc_dir.as_path(),
        "expected zbc from module_paths to win over zpkg in libs_paths"
    );
    assert_eq!(path.extension().and_then(|e| e.to_str()), Some("zbc"));
}
