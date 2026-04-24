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
    assert_eq!(ns(&["Std.IO.Console.WriteLine"]), vec!["Std.IO"]);
}

#[test]
fn multiple_imports_same_namespace_deduplicated() {
    assert_eq!(
        ns(&["Std.IO.Console.WriteLine", "Std.IO.File.ReadText"]),
        vec!["Std.IO"]
    );
}

#[test]
fn imports_from_different_namespaces_all_returned() {
    let result = ns(&["Std.IO.File.ReadText", "Std.Math.Math.Abs"]);
    assert_eq!(result.len(), 2);
    assert!(result.contains(&"Std.IO".to_owned()));
    assert!(result.contains(&"Std.Math".to_owned()));
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

/// Build a minimal binary zpkg (indexed, lib) with a STRS + NSPC section.
/// Layout: header(16) + dir(sec_count×12) + META + STRS + NSPC sections.
fn make_fake_zpkg(dir: &Path, filename: &str, namespaces: &[&str]) {
    use crate::metadata::formats::ZPKG_MAGIC;

    // Build STRS section: one entry per namespace
    let encoded: Vec<Vec<u8>> = namespaces.iter().map(|s| s.as_bytes().to_vec()).collect();
    let mut strs_data: Vec<u8> = Vec::new();
    // count[4]
    strs_data.extend_from_slice(&(encoded.len() as u32).to_le_bytes());
    // entry table: [offset:u32][len:u32]
    let mut offset = 0u32;
    for b in &encoded {
        strs_data.extend_from_slice(&offset.to_le_bytes());
        strs_data.extend_from_slice(&(b.len() as u32).to_le_bytes());
        offset += b.len() as u32;
    }
    // raw data
    for b in &encoded { strs_data.extend_from_slice(b); }

    // Build NSPC section: count[4] + idx[4] per ns
    let mut nspc_data: Vec<u8> = Vec::new();
    nspc_data.extend_from_slice(&(namespaces.len() as u32).to_le_bytes());
    for i in 0u32..namespaces.len() as u32 {
        nspc_data.extend_from_slice(&i.to_le_bytes());
    }

    // Build META section: name, version, entry (each u16-len + bytes)
    let mut meta_data: Vec<u8> = Vec::new();
    for s in &["test", "0.1.0", ""] {
        let b = s.as_bytes();
        meta_data.extend_from_slice(&(b.len() as u16).to_le_bytes());
        meta_data.extend_from_slice(b);
    }

    // Assemble: 3 sections (META, STRS, NSPC)
    let sections: &[(&[u8; 4], &[u8])] = &[
        (b"META", &meta_data),
        (b"STRS", &strs_data),
        (b"NSPC", &nspc_data),
    ];
    let sec_count = sections.len() as u16;
    let header_size: usize = 16;
    let dir_size: usize = sec_count as usize * 12;
    let mut next_offset = (header_size + dir_size) as u32;

    let mut data: Vec<u8> = Vec::new();
    // Header: magic[4] + major[2] + minor[2] + flags[2] + sec_count[2] + reserved[4]
    data.extend_from_slice(&ZPKG_MAGIC);
    data.extend_from_slice(&0u16.to_le_bytes()); // major
    data.extend_from_slice(&1u16.to_le_bytes()); // minor
    data.extend_from_slice(&0u16.to_le_bytes()); // flags: indexed, lib
    data.extend_from_slice(&sec_count.to_le_bytes());
    data.extend_from_slice(&0u32.to_le_bytes()); // reserved

    // Directory
    for (tag, sec) in sections {
        data.extend_from_slice(*tag);
        data.extend_from_slice(&next_offset.to_le_bytes());
        data.extend_from_slice(&(sec.len() as u32).to_le_bytes());
        next_offset += sec.len() as u32;
    }

    // Section data
    for (_, sec) in sections { data.extend_from_slice(sec); }

    std::fs::write(dir.join(filename), &data).expect("write test zpkg");
}

/// Build a minimal binary zbc with just a NSPC section (v0.3 format with directory).
fn make_fake_zbc(dir: &Path, filename: &str, namespace: &str) {
    use crate::metadata::formats::ZBC_MAGIC;
    let ns_bytes = namespace.as_bytes();
    // NSPC section payload: u16(len) + bytes
    let nspc_payload: Vec<u8> = {
        let mut v = Vec::new();
        v.extend_from_slice(&(ns_bytes.len() as u16).to_le_bytes());
        v.extend_from_slice(ns_bytes);
        v
    };

    let sec_count: u16 = 1;
    let header_size: usize = 16;
    let dir_size: usize = sec_count as usize * 12;
    let sec_offset = (header_size + dir_size) as u32;

    let mut data: Vec<u8> = Vec::new();
    // Header: magic[4] + major[2] + minor[2] + flags[2] + sec_count[2] + reserved[4]
    data.extend_from_slice(&ZBC_MAGIC);
    data.extend_from_slice(&0u16.to_le_bytes()); // major
    data.extend_from_slice(&3u16.to_le_bytes()); // minor (v0.3)
    data.extend_from_slice(&0u16.to_le_bytes()); // flags = 0 (full)
    data.extend_from_slice(&sec_count.to_le_bytes());
    data.extend_from_slice(&0u32.to_le_bytes()); // reserved

    // Directory: NSPC entry
    data.extend_from_slice(b"NSPC");
    data.extend_from_slice(&sec_offset.to_le_bytes());
    data.extend_from_slice(&(nspc_payload.len() as u32).to_le_bytes());

    // NSPC section data
    data.extend_from_slice(&nspc_payload);

    std::fs::write(dir.join(filename), &data).expect("write test zbc");
}

/// resolve_namespace with empty paths returns an empty vec
#[test]
fn test_resolve_namespace_empty_paths() {
    let result = resolve_namespace("Std.IO", &[], &[]);
    assert!(result.is_ok());
    assert!(result.unwrap().is_empty());
}

/// Two zpkg files in the same libs tier providing the same namespace
/// → both are returned (legit under C# assembly model; disambiguation
/// happens at the lazy-load layer by zpkg file name).
#[test]
fn test_resolve_namespace_ambiguous_returns_both() {
    let tmp = std::env::temp_dir().join(format!("z42_test_{}", std::process::id()));
    std::fs::create_dir_all(&tmp).unwrap();

    make_fake_zpkg(&tmp, "libA.zpkg", &["z42.conflict"]);
    make_fake_zpkg(&tmp, "libB.zpkg", &["z42.conflict"]);

    let result = resolve_namespace("z42.conflict", &[], &[tmp.clone()]);
    std::fs::remove_dir_all(&tmp).ok();

    assert!(result.is_ok(), "unexpected error: {:?}", result.err());
    let paths = result.unwrap();
    assert_eq!(paths.len(), 2, "both zpkgs should be reported; got {paths:?}");
    let names: std::collections::HashSet<String> = paths
        .iter()
        .filter_map(|p| p.file_name().and_then(|n| n.to_str()).map(str::to_owned))
        .collect();
    assert!(names.contains("libA.zpkg"));
    assert!(names.contains("libB.zpkg"));
}

/// A zbc in module_paths and a zpkg in libs_paths both provide the same namespace
/// → module_paths wins (zpkg tier is skipped when zbc tier has matches).
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
    let paths = result.unwrap();
    assert_eq!(paths.len(), 1, "only zbc should match (zpkg tier skipped)");
    let path = &paths[0];
    assert_eq!(
        path.parent().unwrap(),
        zbc_dir.as_path(),
        "expected zbc from module_paths to win over zpkg in libs_paths"
    );
    assert_eq!(path.extension().and_then(|e| e.to_str()), Some("zbc"));
}

/// resolve_dependency locates a zpkg by file name in the libs_paths.
#[test]
fn test_resolve_dependency_by_file_name() {
    let tmp = std::env::temp_dir().join(format!("z42_test_dep_{}", std::process::id()));
    std::fs::create_dir_all(&tmp).unwrap();
    make_fake_zpkg(&tmp, "z42.fake.zpkg", &["z42.fake"]);

    let hit = crate::metadata::loader::resolve_dependency("z42.fake.zpkg", &[tmp.clone()]).unwrap();
    let miss = crate::metadata::loader::resolve_dependency("does.not.exist.zpkg", &[tmp.clone()]).unwrap();
    std::fs::remove_dir_all(&tmp).ok();

    assert!(hit.is_some());
    assert_eq!(hit.unwrap().file_name().and_then(|n| n.to_str()), Some("z42.fake.zpkg"));
    assert!(miss.is_none());
}
