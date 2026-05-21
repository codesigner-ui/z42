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

// ── is_heap_ref (add-write-barriers, 2026-05-21) ────────────────────────────

use std::collections::HashMap;
use std::sync::Arc;
use crate::gc::GcRef;

fn dummy_type_desc(name: &str) -> Arc<TypeDesc> {
    Arc::new(TypeDesc {
        name: name.to_string(),
        base_name: None,
        fields: Vec::new(),
        field_index: HashMap::new(),
        vtable: Vec::new(),
        vtable_index: HashMap::new(),
        own_fields: Vec::new(),
        own_methods: Vec::new(),
        type_params: vec![],
        type_args: vec![],
        type_param_constraints: vec![],
        id: crate::metadata::tokens::TypeId::UNRESOLVED,
    })
}

#[test]
fn is_heap_ref_true_for_object() {
    let v = Value::Object(GcRef::new(ScriptObject {
        type_desc: dummy_type_desc("Foo"),
        slots: vec![],
        native: NativeData::None,
        type_args: vec![],
    }));
    assert!(v.is_heap_ref());
}

#[test]
fn is_heap_ref_true_for_array() {
    let v = Value::Array(GcRef::new(vec![Value::I64(1), Value::I64(2)]));
    assert!(v.is_heap_ref());
}

#[test]
fn is_heap_ref_true_for_closure() {
    let v = Value::Closure {
        env:     GcRef::new(vec![Value::I64(42)]),
        fn_name: "lambda$0".to_string(),
    };
    assert!(v.is_heap_ref());
}

#[test]
fn is_heap_ref_true_for_ref_array() {
    let arr = GcRef::new(vec![Value::I64(0)]);
    let v = Value::Ref { kind: RefKind::Array { gc_ref: arr, idx: 0 } };
    assert!(v.is_heap_ref());
}

#[test]
fn is_heap_ref_true_for_ref_field() {
    let obj = GcRef::new(ScriptObject {
        type_desc: dummy_type_desc("Foo"),
        slots: vec![Value::I64(0)],
        native: NativeData::None,
        type_args: vec![],
    });
    let v = Value::Ref { kind: RefKind::Field { gc_ref: obj, field_name: "x".to_string() } };
    assert!(v.is_heap_ref());
}

#[test]
fn is_heap_ref_false_for_primitives() {
    assert!(!Value::I64(0).is_heap_ref());
    assert!(!Value::F64(0.0).is_heap_ref());
    assert!(!Value::Bool(true).is_heap_ref());
    assert!(!Value::Char('a').is_heap_ref());
    assert!(!Value::Str("hello".to_string()).is_heap_ref());
    assert!(!Value::Null.is_heap_ref());
}

#[test]
fn is_heap_ref_false_for_func_ref() {
    assert!(!Value::FuncRef("Foo.bar".to_string()).is_heap_ref());
}

#[test]
fn is_heap_ref_false_for_pinned_view() {
    let v = Value::PinnedView { ptr: 0x1000, len: 4, kind: PinSourceKind::ArrayU8 };
    assert!(!v.is_heap_ref());
}

#[test]
fn is_heap_ref_false_for_stack_closure() {
    let v = Value::StackClosure { env_idx: 0, fn_name: "inner".to_string() };
    assert!(!v.is_heap_ref());
}

#[test]
fn is_heap_ref_false_for_ref_stack() {
    let v = Value::Ref { kind: RefKind::Stack { frame_idx: 0, slot: 1 } };
    assert!(!v.is_heap_ref(), "stack ref points to stack location, not heap");
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
