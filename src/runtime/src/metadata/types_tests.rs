/// Unit tests for `metadata::types`: per-tag default value derivation.
///
/// fix-array-default-init, 2026-05-18.

use super::types::*;

#[test]
fn default_for_bool_tag_is_false() {
    assert!(matches!(default_value_for_tag(TAG_BOOL), Value::Bool(false)));
}

#[test]
fn default_for_signed_int_tags_is_zero_i64() {
    for t in [TAG_I8, TAG_I16, TAG_I32, TAG_I64] {
        assert!(matches!(default_value_for_tag(t), Value::I64(0)), "tag {:#x}", t);
    }
}

#[test]
fn default_for_unsigned_int_tags_is_zero_i64() {
    for t in [TAG_U8, TAG_U16, TAG_U32, TAG_U64] {
        assert!(matches!(default_value_for_tag(t), Value::I64(0)), "tag {:#x}", t);
    }
}

#[test]
fn default_for_float_tags_is_zero_f64() {
    for t in [TAG_F32, TAG_F64] {
        match default_value_for_tag(t) {
            Value::F64(v) => assert_eq!(v, 0.0),
            other => panic!("tag {:#x}: expected F64(0.0), got {:?}", t, other),
        }
    }
}

#[test]
fn default_for_char_tag_is_null_char() {
    assert!(matches!(default_value_for_tag(TAG_CHAR), Value::Char('\0')));
}

#[test]
fn default_for_ref_tags_is_null() {
    for t in [TAG_STR, TAG_OBJECT, TAG_ARRAY, TAG_UNKNOWN, 0xFF] {
        assert!(matches!(default_value_for_tag(t), Value::Null), "tag {:#x}", t);
    }
}

#[test]
fn default_value_for_string_keys_match_tags() {
    // Sanity: the string-keyed and byte-keyed lookups stay in sync for the
    // primitive types — drift here would silently regress per-type defaults
    // across the two call paths (FieldSlot vs ArrayNew).
    assert!(matches!(default_value_for("bool"), Value::Bool(false)));
    assert!(matches!(default_value_for("int"),  Value::I64(0)));
    assert!(matches!(default_value_for("long"), Value::I64(0)));
    assert!(matches!(default_value_for("byte"), Value::I64(0)));
    assert!(matches!(default_value_for("char"), Value::Char('\0')));
    assert!(matches!(default_value_for("string"), Value::Null));
    match default_value_for("double") {
        Value::F64(v) => assert_eq!(v, 0.0),
        other => panic!("expected F64(0.0), got {:?}", other),
    }
}
