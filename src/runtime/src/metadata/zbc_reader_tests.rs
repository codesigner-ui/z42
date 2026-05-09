//! Phase 3 S3 (tokenize-ir-and-zbc-bump, 2026-05-09) IdMap unit tests.
//!
//! IdMap is the v1.0 wire-format token decoder. Pre-1.0 support was dropped
//! in S3c per CLAUDE.md "不为旧版本提供兼容".

use super::*;

fn pool_for_test() -> Vec<String> {
    vec![
        "Demo.Aaa".to_owned(),     // pool[0]
        "Std.IO.Print".to_owned(), // pool[1]
        "Std.Math.Abs".to_owned(), // pool[2]
    ]
}

#[test]
fn local_token_uses_local_funcs_table() {
    let pool = pool_for_test();
    let id_map = IdMap::for_v1(
        &pool,
        vec!["Demo.Main".to_owned(), "Demo.Helper".to_owned()],
        vec!["Demo.Foo".to_owned()],
    );

    // token < IMPORT_BASE → local index.
    assert_eq!(id_map.resolve_method(0).unwrap(), "Demo.Main");
    assert_eq!(id_map.resolve_method(1).unwrap(), "Demo.Helper");
}

#[test]
fn import_token_uses_pool() {
    let pool = pool_for_test();
    let id_map = IdMap::for_v1(
        &pool,
        vec!["Demo.Main".to_owned()],
        vec![],
    );

    // token >= IMPORT_BASE → pool[token - IMPORT_BASE].
    assert_eq!(id_map.resolve_method(IMPORT_BASE_TOKEN + 1).unwrap(), "Std.IO.Print");
    assert_eq!(id_map.resolve_method(IMPORT_BASE_TOKEN + 2).unwrap(), "Std.Math.Abs");
}

#[test]
fn local_class_token_uses_local_classes_table() {
    let pool = pool_for_test();
    let id_map = IdMap::for_v1(
        &pool,
        vec![],
        vec!["Demo.Foo".to_owned(), "Demo.Bar".to_owned()],
    );

    assert_eq!(id_map.resolve_type(0).unwrap(), "Demo.Foo");
    assert_eq!(id_map.resolve_type(1).unwrap(), "Demo.Bar");
}

#[test]
fn unresolved_token_returns_diagnostic_placeholder() {
    let pool = pool_for_test();
    let id_map = IdMap::for_v1(&pool, vec![], vec![]);

    // UNRESOLVED is encoded as 0xFFFF_FFFF, separately handled (not a pool idx).
    assert_eq!(id_map.resolve_method(UNRESOLVED_TOKEN).unwrap(), "<unresolved>");
    assert_eq!(id_map.resolve_type(UNRESOLVED_TOKEN).unwrap(), "<unresolved>");
}

#[test]
fn local_token_oob_errors() {
    let pool = pool_for_test();
    let id_map = IdMap::for_v1(
        &pool,
        vec!["only_one".to_owned()],
        vec![],
    );
    assert!(id_map.resolve_method(99).is_err()); // local OOB
}

#[test]
fn import_token_to_oob_pool_errors() {
    let pool = pool_for_test();
    let id_map = IdMap::for_v1(&pool, vec![], vec![]);

    // IMPORT_BASE + huge → pool OOB
    let bad = IMPORT_BASE_TOKEN + 999;
    assert!(bad < UNRESOLVED_TOKEN); // not the UNRESOLVED sentinel
    assert!(id_map.resolve_method(bad).is_err());
}
