//! Tests for the Z#### catalog.
//!
//! Includes a **drift guard** that walks `src/runtime/src/` looking for
//! `Z[0-9]{4}` literals and asserts every one is present in `Z.json`,
//! and that every catalog entry has at least one emit site.

use super::*;
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

fn runtime_src_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("src")
}

#[test]
fn explain_returns_known_entries() {
    let e = explain("Z0905").expect("Z0905 should be in catalog");
    assert!(e.title.contains("Native type registration"));
    let e = explain("Z0910").expect("Z0910 should be in catalog");
    assert!(e.title.contains("Native library load"));
}

#[test]
fn explain_unknown_returns_none() {
    assert!(explain("Z9999").is_none());
}

#[test]
fn list_all_has_at_least_five_entries() {
    let n = list_all().len();
    assert!(n >= 5, "expected ≥ 5 entries, got {n}");
}

#[test]
fn format_includes_header_and_description() {
    let e = explain("Z0905").unwrap();
    let text = format(e);
    assert!(text.starts_with("error[Z0905]:"));
    assert!(text.contains("Native type registration failure"));
    assert!(text.contains("─"));   // horizontal rule
}

// ── Drift guard: catalog ↔ source emit sites ──

#[test]
fn every_emit_site_has_catalog_entry() {
    let used = scan_z_codes_in_runtime_src();
    let cataloged: HashSet<String> = list_all().iter().map(|e| e.code.clone()).collect();

    let undocumented: Vec<&String> = used.iter()
        .filter(|c| !cataloged.contains(*c))
        .collect();

    assert!(
        undocumented.is_empty(),
        "Z codes used in src/runtime/src/ but missing from docs/error-codes/Z.json:\n  {undocumented:?}"
    );
}

#[test]
fn every_catalog_entry_has_emit_site() {
    let used = scan_z_codes_in_runtime_src();
    let cataloged: HashSet<String> = list_all().iter().map(|e| e.code.clone()).collect();

    let unused: Vec<&String> = cataloged.iter()
        .filter(|c| !used.contains(*c))
        .collect();

    assert!(
        unused.is_empty(),
        "Z codes in docs/error-codes/Z.json but not emitted anywhere in src/runtime/src/:\n  {unused:?}"
    );
}

// ── Helpers ──

fn scan_z_codes_in_runtime_src() -> HashSet<String> {
    let mut found = HashSet::new();

    visit_rs_files(&runtime_src_root(), &mut |path, contents| {
        // Skip the catalog module itself + tests/_tests files (they reference
        // codes for assertions, not as emit sites).
        let path_str = path.to_string_lossy();
        if path_str.contains("/diagnostics/") { return; }
        if path_str.ends_with("_tests.rs")     { return; }

        find_z_codes_in(contents, &mut found);
    });

    found
}

/// Manual scan for `Z` followed by exactly 4 ASCII digits. Avoids pulling
/// in the `regex` crate just for this drift check.
fn find_z_codes_in(text: &str, out: &mut HashSet<String>) {
    let bytes = text.as_bytes();
    let mut i = 0;
    while i + 5 <= bytes.len() {
        if bytes[i] == b'Z'
            && bytes[i + 1].is_ascii_digit()
            && bytes[i + 2].is_ascii_digit()
            && bytes[i + 3].is_ascii_digit()
            && bytes[i + 4].is_ascii_digit()
        {
            // Ensure this isn't part of a longer identifier (e.g. "ZZ12345").
            let next_ok = i + 5 == bytes.len() || !bytes[i + 5].is_ascii_alphanumeric();
            let prev_ok = i == 0 || !bytes[i - 1].is_ascii_alphanumeric();
            if next_ok && prev_ok {
                out.insert(std::str::from_utf8(&bytes[i..i + 5]).unwrap().to_string());
            }
            i += 5;
        } else {
            i += 1;
        }
    }
}

fn visit_rs_files(dir: &Path, cb: &mut dyn FnMut(&Path, &str)) {
    let entries = match fs::read_dir(dir) {
        Ok(e) => e,
        Err(_) => return,
    };
    for entry in entries.flatten() {
        let p = entry.path();
        if p.is_dir() {
            visit_rs_files(&p, cb);
        } else if p.extension().map(|e| e == "rs").unwrap_or(false) {
            if let Ok(contents) = fs::read_to_string(&p) {
                cb(&p, &contents);
            }
        }
    }
}
