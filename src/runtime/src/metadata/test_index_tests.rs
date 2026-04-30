//! Tests for `test_index` decoder (v=2 layout).

use super::*;

/// Helper to build a TIDX payload with `entries` (fixed-width LE, v=2),
/// matching the C# ZbcWriter side.
fn build_payload(entries: &[TestEntry]) -> Vec<u8> {
    let mut out = Vec::new();
    // magic — bytes "TIDX" on disk; little-endian u32 reads as 0x58444954
    out.extend_from_slice(b"TIDX");
    out.push(TEST_INDEX_VERSION);
    out.extend_from_slice(&(entries.len() as u32).to_le_bytes());
    for e in entries {
        out.extend_from_slice(&e.method_id.to_le_bytes());
        out.push(e.kind as u8);
        out.extend_from_slice(&e.flags.bits().to_le_bytes());
        out.extend_from_slice(&e.skip_reason_str_idx.to_le_bytes());
        out.extend_from_slice(&e.skip_platform_str_idx.to_le_bytes());
        out.extend_from_slice(&e.skip_feature_str_idx.to_le_bytes());
        out.extend_from_slice(&e.expected_throw_type_idx.to_le_bytes());
        out.extend_from_slice(&(e.test_cases.len() as u32).to_le_bytes());
        for tc in &e.test_cases {
            out.extend_from_slice(&tc.arg_repr_str_idx.to_le_bytes());
        }
    }
    out
}

/// Convenience constructor for tests; defaults all str-idx fields to 0 and
/// resolved string fields to None (not yet resolved by loader).
fn entry(method_id: u32, kind: TestEntryKind) -> TestEntry {
    TestEntry {
        method_id,
        kind,
        flags: TestFlags::empty(),
        skip_reason_str_idx: 0,
        skip_platform_str_idx: 0,
        skip_feature_str_idx: 0,
        expected_throw_type_idx: 0,
        test_cases: vec![],
        skip_reason: None,
        skip_platform: None,
        skip_feature: None,
        expected_throw_type: None,
    }
}

#[test]
fn decodes_empty_section() {
    let payload = build_payload(&[]);
    let result = read_test_index(&payload).unwrap();
    assert!(result.is_empty());
}

#[test]
fn decodes_single_test_entry() {
    let e = entry(42, TestEntryKind::Test);
    let payload = build_payload(&[e.clone()]);
    let result = read_test_index(&payload).unwrap();
    assert_eq!(result.len(), 1);
    assert_eq!(result[0], e);
}

#[test]
fn decodes_all_kind_variants() {
    let entries = vec![
        entry(1, TestEntryKind::Test),
        entry(2, TestEntryKind::Benchmark),
        entry(3, TestEntryKind::Setup),
        entry(4, TestEntryKind::Teardown),
        entry(5, TestEntryKind::Doctest),
    ];
    let payload = build_payload(&entries);
    let result = read_test_index(&payload).unwrap();
    assert_eq!(result, entries);
}

#[test]
fn decodes_flags_and_indices() {
    let e = TestEntry {
        method_id: 7,
        kind: TestEntryKind::Test,
        flags: TestFlags::SKIPPED | TestFlags::SHOULD_THROW,
        skip_reason_str_idx: 11,
        skip_platform_str_idx: 0,
        skip_feature_str_idx: 0,
        expected_throw_type_idx: 22,
        test_cases: vec![],
        skip_reason: None,
        skip_platform: None,
        skip_feature: None,
        expected_throw_type: None,
    };
    let payload = build_payload(&[e.clone()]);
    let result = read_test_index(&payload).unwrap();
    assert_eq!(result[0], e);
    assert!(result[0].flags.contains(TestFlags::SKIPPED));
    assert!(result[0].flags.contains(TestFlags::SHOULD_THROW));
    assert!(!result[0].flags.contains(TestFlags::IGNORED));
}

#[test]
fn decodes_skip_platform_and_feature() {
    // R1.C: [Skip(platform: ..., feature: ..., reason: ...)] all three populated.
    let e = TestEntry {
        method_id: 8,
        kind: TestEntryKind::Test,
        flags: TestFlags::SKIPPED,
        skip_reason_str_idx: 100,
        skip_platform_str_idx: 200,  // e.g. pool[200] = "ios"
        skip_feature_str_idx: 300,   // e.g. pool[300] = "jit"
        expected_throw_type_idx: 0,
        test_cases: vec![],
        skip_reason: None,
        skip_platform: None,
        skip_feature: None,
        expected_throw_type: None,
    };
    let payload = build_payload(&[e.clone()]);
    let result = read_test_index(&payload).unwrap();
    assert_eq!(result[0], e);
    assert_eq!(result[0].skip_platform_str_idx, 200);
    assert_eq!(result[0].skip_feature_str_idx, 300);
}

#[test]
fn decodes_test_cases() {
    let e = TestEntry {
        method_id: 9,
        kind: TestEntryKind::Test,
        flags: TestFlags::empty(),
        skip_reason_str_idx: 0,
        skip_platform_str_idx: 0,
        skip_feature_str_idx: 0,
        expected_throw_type_idx: 0,
        test_cases: vec![
            TestCase { arg_repr_str_idx: 100 },
            TestCase { arg_repr_str_idx: 101 },
            TestCase { arg_repr_str_idx: 102 },
        ],
        skip_reason: None,
        skip_platform: None,
        skip_feature: None,
        expected_throw_type: None,
    };
    let payload = build_payload(&[e.clone()]);
    let result = read_test_index(&payload).unwrap();
    assert_eq!(result[0].test_cases.len(), 3);
    assert_eq!(result[0], e);
}

#[test]
fn rejects_wrong_magic() {
    let mut payload = build_payload(&[]);
    payload[0] = b'X'; // corrupt first magic byte
    let err = read_test_index(&payload).unwrap_err();
    assert!(format!("{err}").contains("invalid TIDX magic"), "got: {err}");
}

#[test]
fn rejects_unsupported_version() {
    let mut payload = build_payload(&[]);
    payload[4] = 99; // version byte (after magic)
    let err = read_test_index(&payload).unwrap_err();
    assert!(format!("{err}").contains("unsupported TIDX version"), "got: {err}");
}

#[test]
fn rejects_v1_explicitly() {
    // Reject v=1 specifically — pre-1.0 bumped before any v=1 file in the wild;
    // a stale v=1 file would now be invalid.
    let mut payload = build_payload(&[]);
    payload[4] = 1;
    let err = read_test_index(&payload).unwrap_err();
    assert!(format!("{err}").contains("unsupported TIDX version"), "got: {err}");
}

#[test]
fn rejects_unknown_kind_discriminant() {
    let e = entry(1, TestEntryKind::Test);
    let mut payload = build_payload(&[e]);
    // Locate the kind byte: magic(4) + version(1) + entry_count(4) + method_id(4) = offset 13
    payload[13] = 99; // unknown discriminant
    let err = read_test_index(&payload).unwrap_err();
    assert!(format!("{err}").contains("unknown TestEntryKind discriminant"), "got: {err}");
}

#[test]
fn rejects_truncated_payload() {
    let payload = vec![b'T', b'I', b'D']; // missing 4th byte of magic
    let err = read_test_index(&payload).unwrap_err();
    assert!(format!("{err}").contains("truncated"), "got: {err}");
}

#[test]
fn rejects_reserved_flag_bits() {
    let e = entry(1, TestEntryKind::Test);
    let mut payload = build_payload(&[e]);
    // Locate flags bytes: magic(4) + version(1) + entry_count(4) + method_id(4) + kind(1) = offset 14-15
    payload[14] = 0xFF;
    payload[15] = 0xFF;
    let err = read_test_index(&payload).unwrap_err();
    assert!(format!("{err}").contains("reserved flag"), "got: {err}");
}
