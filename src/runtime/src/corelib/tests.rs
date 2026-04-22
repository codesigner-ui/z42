use super::*;
use crate::metadata::{FieldSlot, NativeData, ScriptObject, TypeDesc, Value};
use std::collections::HashMap;
use std::sync::Arc;

fn s(v: &str) -> Value { Value::Str(v.into()) }
fn i(n: i64) -> Value { Value::I64(n) }
fn i64(n: i64) -> Value { Value::I64(n) }
fn obj(class_name: &str) -> Value {
    let type_desc = Arc::new(TypeDesc {
        name: class_name.to_string(),
        base_name: None,
        fields: Vec::new(),
        field_index: HashMap::new(),
        vtable: Vec::new(),
        vtable_index: HashMap::new(), type_params: vec![], type_args: vec![],
        type_param_constraints: vec![],
    });
    Value::Object(std::rc::Rc::new(std::cell::RefCell::new(ScriptObject {
        type_desc,
        slots: Vec::new(),
        native: NativeData::None,
    })))
}

// ── __len ─────────────────────────────────────────────────────────────────────

#[test]
fn len_of_string_is_utf8_bytes() {
    assert_eq!(exec_builtin("__len", &[s("hello")]).unwrap(), i64(5));
}

#[test]
fn len_of_empty_string() {
    assert_eq!(exec_builtin("__len", &[s("")]).unwrap(), i64(0));
}

#[test]
fn len_missing_arg_errors() {
    assert!(exec_builtin("__len", &[]).is_err());
}

// ── __str_substring ───────────────────────────────────────────────────────────

#[test]
fn substring_one_arg() {
    assert_eq!(
        exec_builtin("__str_substring", &[s("Hello, World!"), i(7)]).unwrap(),
        s("World!")
    );
}

#[test]
fn substring_two_args() {
    assert_eq!(
        exec_builtin("__str_substring", &[s("Hello, World!"), i(7), i(5)]).unwrap(),
        s("World")
    );
}

#[test]
fn substring_out_of_range_errors() {
    assert!(exec_builtin("__str_substring", &[s("hi"), i(10)]).is_err());
}

// ── __str_contains ────────────────────────────────────────────────────────────

#[test]
fn contains_true() {
    assert_eq!(
        exec_builtin("__str_contains", &[s("Hello, World!"), s("World")]).unwrap(),
        Value::Bool(true)
    );
}

#[test]
fn contains_false() {
    assert_eq!(
        exec_builtin("__str_contains", &[s("Hello"), s("world")]).unwrap(),
        Value::Bool(false)
    );
}

// ── __str_starts_with ─────────────────────────────────────────────────────────

#[test]
fn starts_with_true() {
    assert_eq!(
        exec_builtin("__str_starts_with", &[s("Hello, World!"), s("Hello")]).unwrap(),
        Value::Bool(true)
    );
}

#[test]
fn starts_with_false() {
    assert_eq!(
        exec_builtin("__str_starts_with", &[s("Hello"), s("World")]).unwrap(),
        Value::Bool(false)
    );
}

// ── __str_ends_with ───────────────────────────────────────────────────────────

#[test]
fn ends_with_true() {
    assert_eq!(
        exec_builtin("__str_ends_with", &[s("Hello, World!"), s("!")]).unwrap(),
        Value::Bool(true)
    );
}

#[test]
fn ends_with_false() {
    assert_eq!(
        exec_builtin("__str_ends_with", &[s("Hello"), s("World")]).unwrap(),
        Value::Bool(false)
    );
}

// ── dispatch table coverage ───────────────────────────────────────────────────

#[test]
fn unknown_builtin_errors() {
    assert!(exec_builtin("__nonexistent", &[]).is_err());
}

#[test]
fn println_via_dispatch_table() {
    assert!(exec_builtin("__println", &[s("test")]).is_ok());
}

// ── assert ────────────────────────────────────────────────────────────────────

#[test]
fn assert_eq_success() {
    assert!(exec_builtin("__assert_eq", &[i64(42), i64(42)]).is_ok());
}

#[test]
fn assert_eq_failure() {
    assert!(exec_builtin("__assert_eq", &[i64(1), i64(2)]).is_err());
}

// ── __obj_get_type ────────────────────────────────────────────────────────────

#[test]
fn obj_get_type_returns_type_object() {
    let result = exec_builtin("__obj_get_type", &[obj("Foo")]).unwrap();
    match result {
        Value::Object(rc) => assert_eq!(rc.borrow().type_desc.name, "Std.Type"),
        other => panic!("expected Object, got {:?}", other),
    }
}

#[test]
fn obj_get_type_simple_name_no_namespace() {
    let result = exec_builtin("__obj_get_type", &[obj("Foo")]).unwrap();
    let Value::Object(rc) = result else { panic!("expected Object") };
    let borrow = rc.borrow();
    assert_eq!(borrow.slots[0], Value::Str("Foo".into()));
    assert_eq!(borrow.slots[1], Value::Str("Foo".into()));
}

#[test]
fn obj_get_type_namespaced_class_splits_name() {
    let result = exec_builtin("__obj_get_type", &[obj("geometry.Circle")]).unwrap();
    let Value::Object(rc) = result else { panic!("expected Object") };
    let borrow = rc.borrow();
    assert_eq!(borrow.slots[0], Value::Str("Circle".into()));
    assert_eq!(borrow.slots[1], Value::Str("geometry.Circle".into()));
}

#[test]
fn obj_get_type_null_errors() {
    assert!(exec_builtin("__obj_get_type", &[Value::Null]).is_err());
}

#[test]
fn obj_get_type_non_object_errors() {
    assert!(exec_builtin("__obj_get_type", &[i(42)]).is_err());
}

// ── __obj_ref_eq ──────────────────────────────────────────────────────────────

#[test]
fn obj_ref_eq_same_rc_is_true() {
    let a = obj("Foo");
    assert_eq!(
        exec_builtin("__obj_ref_eq", &[a.clone(), a]).unwrap(),
        Value::Bool(true)
    );
}

#[test]
fn obj_ref_eq_different_allocs_is_false() {
    assert_eq!(
        exec_builtin("__obj_ref_eq", &[obj("Foo"), obj("Foo")]).unwrap(),
        Value::Bool(false)
    );
}

#[test]
fn obj_ref_eq_both_null_is_true() {
    assert_eq!(
        exec_builtin("__obj_ref_eq", &[Value::Null, Value::Null]).unwrap(),
        Value::Bool(true)
    );
}

#[test]
fn obj_ref_eq_one_null_is_false() {
    assert_eq!(
        exec_builtin("__obj_ref_eq", &[obj("Foo"), Value::Null]).unwrap(),
        Value::Bool(false)
    );
    assert_eq!(
        exec_builtin("__obj_ref_eq", &[Value::Null, obj("Foo")]).unwrap(),
        Value::Bool(false)
    );
}

// ── __obj_hash_code ───────────────────────────────────────────────────────────

#[test]
fn obj_hash_code_returns_i32() {
    let result = exec_builtin("__obj_hash_code", &[obj("Foo")]).unwrap();
    assert!(matches!(result, Value::I64(_)));
}

#[test]
fn obj_hash_code_same_object_is_consistent() {
    let a = obj("Foo");
    let h1 = exec_builtin("__obj_hash_code", &[a.clone()]).unwrap();
    let h2 = exec_builtin("__obj_hash_code", &[a]).unwrap();
    assert_eq!(h1, h2);
}

#[test]
fn obj_hash_code_null_is_zero() {
    assert_eq!(
        exec_builtin("__obj_hash_code", &[Value::Null]).unwrap(),
        Value::I64(0)
    );
}

#[test]
fn obj_hash_code_non_object_errors() {
    assert!(exec_builtin("__obj_hash_code", &[i(1)]).is_err());
}
