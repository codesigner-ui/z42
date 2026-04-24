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

// ── install / uninstall ───────────────────────────────────────────────────────

#[test]
fn install_then_uninstall_is_clean() {
    install(None, 0);
    assert!(try_lookup_function("Std.IO.Console.WriteLine").is_none());
    uninstall();
}

#[test]
fn install_with_deps_no_libs_no_declared_returns_none() {
    install_with_deps(None, 0, Vec::new(), Vec::new());
    assert!(try_lookup_function("Std.Anything.F").is_none());
    assert!(try_lookup_type("Std.Anything").is_none());
    uninstall();
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
        None,
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
        None,
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
        None,
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
        None,
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
        None,
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
        None,
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
