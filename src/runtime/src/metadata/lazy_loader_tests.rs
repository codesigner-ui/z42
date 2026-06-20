use super::*;
use std::path::PathBuf;

// ── namespace_prefix ──────────────────────────────────────────────────────────

#[test]
fn namespace_prefix_of_qualified_call() {
    assert_eq!(
        namespace_prefix("Std.IO.Console.WriteLine"),
        Some("Std.IO".to_string())
    );
    assert_eq!(
        namespace_prefix("Std.Text.StringBuilder.Append$1"),
        Some("Std.Text".to_string())
    );
}

#[test]
fn namespace_prefix_of_deep_call() {
    // "Std.Collections.Stack.Push" → "Std.Collections" (W1 routing key)
    assert_eq!(
        namespace_prefix("Std.Collections.Stack.Push"),
        Some("Std.Collections".to_string())
    );
}

#[test]
fn namespace_prefix_of_shallow_name() {
    assert_eq!(namespace_prefix("Assert.Equal"), Some("Assert".to_string()));
    assert_eq!(namespace_prefix("main"), None);
}

// ── candidates_for_namespace (strategy C routing) ────────────────────────────

fn fake_candidate(namespaces: &[&str]) -> ZpkgCandidate {
    ZpkgCandidate {
        file_path:  PathBuf::from("/does/not/matter"),
        namespaces: namespaces.iter().map(|s| s.to_string()).collect(),
    }
}

#[test]
fn candidates_routes_by_exact_namespace() {
    let loader = LazyLoader::new(
        Vec::new(),
        0,
        vec![
            ("a.zpkg".to_string(), fake_candidate(&["Std.IO"])),
            ("b.zpkg".to_string(), fake_candidate(&["Std.Collections"])),
        ],
        Vec::new(),
    );
    let matches = loader.candidates_for_namespace("Std.Collections");
    assert_eq!(matches, vec!["b.zpkg".to_string()]);
}

#[test]
fn candidates_routes_by_descendant_namespace() {
    // Querying `Std.Collections` should match a zpkg declaring
    // `Std.Collections.Generic` (descendant prefix match).
    let loader = LazyLoader::new(
        Vec::new(),
        0,
        vec![
            ("a.zpkg".to_string(), fake_candidate(&["Std.Collections.Generic"])),
            ("b.zpkg".to_string(), fake_candidate(&["Std.IO"])),
        ],
        Vec::new(),
    );
    let matches = loader.candidates_for_namespace("Std.Collections");
    assert_eq!(matches, vec!["a.zpkg".to_string()]);
}

/// W1 regression guard: two zpkgs legitimately share `Std.Collections`
/// (`z42.core` declares it for List/Dictionary; `z42.collections` declares
/// it for Queue/Stack). Both must be routed as candidates — no ambiguity
/// error at this layer.
#[test]
fn candidates_routes_multi_zpkg_sharing_namespace() {
    let loader = LazyLoader::new(
        Vec::new(),
        0,
        vec![
            (
                "z42.core.zpkg".to_string(),
                fake_candidate(&["Std", "Std.Collections"]),
            ),
            (
                "z42.collections.zpkg".to_string(),
                fake_candidate(&["Std.Collections"]),
            ),
        ],
        Vec::new(),
    );
    let mut matches = loader.candidates_for_namespace("Std.Collections");
    matches.sort();
    assert_eq!(
        matches,
        vec![
            "z42.collections.zpkg".to_string(),
            "z42.core.zpkg".to_string(),
        ]
    );
}

#[test]
fn install_filters_already_loaded_from_declared() {
    let loader = LazyLoader::new(
        Vec::new(),
        0,
        vec![(
            "z42.collections.zpkg".to_string(),
            fake_candidate(&["Std.Collections"]),
        )],
        vec!["z42.collections.zpkg".to_string()], // already loaded
    );
    assert!(loader.declared_zpkgs.is_empty());
    assert!(loader.candidates_for_namespace("Std.Collections").is_empty());
}

#[test]
fn remaining_declared_excludes_loaded() {
    let mut loader = LazyLoader::new(
        Vec::new(),
        0,
        vec![
            ("a.zpkg".to_string(), fake_candidate(&["X"])),
            ("b.zpkg".to_string(), fake_candidate(&["Y"])),
        ],
        Vec::new(),
    );
    loader.loaded_zpkgs.insert("a.zpkg".to_string());
    let mut r = loader.remaining_declared();
    r.sort();
    assert_eq!(r, vec!["b.zpkg".to_string()]);
}

#[test]
fn candidates_excludes_subsequently_loaded() {
    let mut loader = LazyLoader::new(
        Vec::new(),
        0,
        vec![(
            "a.zpkg".to_string(),
            fake_candidate(&["Std.Collections"]),
        )],
        Vec::new(),
    );
    // Initially routed as candidate.
    assert_eq!(
        loader.candidates_for_namespace("Std.Collections"),
        vec!["a.zpkg".to_string()]
    );
    // After marking loaded, no longer a candidate (Decision 4 idempotency).
    loader.loaded_zpkgs.insert("a.zpkg".to_string());
    assert!(loader.candidates_for_namespace("Std.Collections").is_empty());
}

// ── VmContext-based install / uninstall (replaces former thread_local API) ───

#[test]
fn vm_context_install_then_uninstall_is_clean() {
    let ctx = crate::vm_context::VmContext::new();
    ctx.install_lazy_loader(None, 0);
    assert!(ctx.try_lookup_function("Std.IO.Console.WriteLine").is_none());
    ctx.uninstall_lazy_loader();
    assert!(ctx.try_lookup_function("Anything.Foo").is_none());
}

#[test]
fn vm_context_install_with_deps_no_libs_no_declared_returns_none() {
    let ctx = crate::vm_context::VmContext::new();
    ctx.install_lazy_loader_with_deps(Vec::new(), 0, Vec::new(), Vec::new());
    assert!(ctx.try_lookup_function("Std.Anything.F").is_none());
    assert!(ctx.try_lookup_type("Std.Anything").is_none());
    ctx.uninstall_lazy_loader();
}

// ── build_in_dirs: colocated dep search (support-colocated-zpkg-deps) ─────────

/// Path to a committed, valid zpkg fixture usable as a real on-disk zpkg.
fn fixture_zpkg() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../tests/zpkg-format/packed-minimal/source.zpkg")
}

#[test]
fn build_in_dirs_not_found_errors() {
    let a = std::env::temp_dir().join("z42-coloc-a-empty");
    let _ = std::fs::create_dir_all(&a);
    assert!(ZpkgCandidate::build_in_dirs(&[a], "nope.zpkg").is_err());
    assert!(ZpkgCandidate::build_in_dirs(&[], "nope.zpkg").is_err());
}

#[test]
fn build_in_dirs_finds_in_later_dir() {
    // Two dirs; the zpkg lives only in the SECOND — `build_in_dirs` must skip
    // the first and resolve from the second (colocated-dep search semantics).
    let base = std::env::temp_dir().join("z42-coloc-order");
    let dir1 = base.join("empty");
    let dir2 = base.join("has");
    let _ = std::fs::create_dir_all(&dir1);
    let _ = std::fs::create_dir_all(&dir2);
    let target = dir2.join("colo.zpkg");
    std::fs::copy(fixture_zpkg(), &target).expect("copy fixture zpkg");

    let cand = ZpkgCandidate::build_in_dirs(&[dir1.clone(), dir2.clone()], "colo.zpkg")
        .expect("resolves from the second dir");
    assert_eq!(cand.file_path, target, "resolved from the dir that actually has the file");
}

#[test]
fn build_in_dirs_first_dir_wins() {
    // When present in BOTH dirs, the FIRST listed dir wins (deterministic order).
    let base = std::env::temp_dir().join("z42-coloc-firstwins");
    let dir1 = base.join("first");
    let dir2 = base.join("second");
    let _ = std::fs::create_dir_all(&dir1);
    let _ = std::fs::create_dir_all(&dir2);
    std::fs::copy(fixture_zpkg(), dir1.join("dup.zpkg")).unwrap();
    std::fs::copy(fixture_zpkg(), dir2.join("dup.zpkg")).unwrap();

    let cand = ZpkgCandidate::build_in_dirs(&[dir1.clone(), dir2.clone()], "dup.zpkg").unwrap();
    assert_eq!(cand.file_path, dir1.join("dup.zpkg"), "first search dir wins on conflict");
}
