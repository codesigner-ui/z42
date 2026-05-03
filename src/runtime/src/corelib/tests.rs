use super::*;
use crate::metadata::{NativeData, TypeDesc, Value};
use crate::vm_context::VmContext;
use std::collections::HashMap;
use std::sync::Arc;

fn s(v: &str) -> Value { Value::Str(v.into()) }
fn i(n: i64) -> Value { Value::I64(n) }
fn i64(n: i64) -> Value { Value::I64(n) }

/// Build a fresh VmContext for each test (heap is fully isolated, fast to construct).
fn ctx() -> VmContext { VmContext::new() }

/// Allocate a minimal Object with the given class name through the heap interface.
fn obj(ctx: &VmContext, class_name: &str) -> Value {
    let type_desc = Arc::new(TypeDesc {
        name: class_name.to_string(),
        base_name: None,
        fields: Vec::new(),
        field_index: HashMap::new(),
        vtable: Vec::new(),
        vtable_index: HashMap::new(), type_params: vec![], type_args: vec![],
        type_param_constraints: vec![],
    });
    ctx.heap().alloc_object(type_desc, Vec::new(), NativeData::None)
}

// ── __len ─────────────────────────────────────────────────────────────────────

#[test]
fn len_of_string_is_utf8_bytes() {
    let c = ctx();
    assert_eq!(exec_builtin(&c, "__len", &[s("hello")]).unwrap(), i64(5));
}

#[test]
fn len_of_empty_string() {
    let c = ctx();
    assert_eq!(exec_builtin(&c, "__len", &[s("")]).unwrap(), i64(0));
}

#[test]
fn len_missing_arg_errors() {
    let c = ctx();
    assert!(exec_builtin(&c, "__len", &[]).is_err());
}

// ── __str_char_at (new in simplify-string-stdlib 2026-04-24) ──────────────────

#[test]
fn char_at_returns_nth_scalar() {
    let c = ctx();
    assert_eq!(exec_builtin(&c, "__str_char_at", &[s("hello"), i(1)]).unwrap(), Value::Char('e'));
    assert_eq!(exec_builtin(&c, "__str_char_at", &[s("hello"), i(0)]).unwrap(), Value::Char('h'));
    assert_eq!(exec_builtin(&c, "__str_char_at", &[s("hello"), i(4)]).unwrap(), Value::Char('o'));
}

#[test]
fn char_at_out_of_range_errors() {
    let c = ctx();
    assert!(exec_builtin(&c, "__str_char_at", &[s("abc"), i(5)]).is_err());
}

#[test]
fn char_at_unicode_scalar_index() {
    // "α" is one scalar but 2 UTF-8 bytes; script-level API treats it as one unit.
    let c = ctx();
    assert_eq!(exec_builtin(&c, "__str_char_at", &[s("αβγ"), i(1)]).unwrap(), Value::Char('β'));
}

// ── __str_from_chars (new in simplify-string-stdlib 2026-04-24) ──────────────

#[test]
fn from_chars_builds_string() {
    let c = ctx();
    let arr = c.heap().alloc_array(vec![
        Value::Char('h'), Value::Char('i'),
    ]);
    assert_eq!(exec_builtin(&c, "__str_from_chars", &[arr]).unwrap(), s("hi"));
}

#[test]
fn from_chars_empty_array() {
    let c = ctx();
    let arr = c.heap().alloc_array(vec![]);
    assert_eq!(exec_builtin(&c, "__str_from_chars", &[arr]).unwrap(), s(""));
}

// ── __char_is_whitespace / __char_to_lower / __char_to_upper ─────────────────

#[test]
fn char_is_whitespace_ascii() {
    let c = ctx();
    assert_eq!(exec_builtin(&c, "__char_is_whitespace", &[Value::Char(' ')]).unwrap(), Value::Bool(true));
    assert_eq!(exec_builtin(&c, "__char_is_whitespace", &[Value::Char('\t')]).unwrap(), Value::Bool(true));
    assert_eq!(exec_builtin(&c, "__char_is_whitespace", &[Value::Char('\n')]).unwrap(), Value::Bool(true));
    assert_eq!(exec_builtin(&c, "__char_is_whitespace", &[Value::Char('a')]).unwrap(), Value::Bool(false));
}

#[test]
fn char_to_lower_ascii_only() {
    let c = ctx();
    assert_eq!(exec_builtin(&c, "__char_to_lower", &[Value::Char('A')]).unwrap(), Value::Char('a'));
    assert_eq!(exec_builtin(&c, "__char_to_lower", &[Value::Char('Z')]).unwrap(), Value::Char('z'));
    assert_eq!(exec_builtin(&c, "__char_to_lower", &[Value::Char('1')]).unwrap(), Value::Char('1'));
    assert_eq!(exec_builtin(&c, "__char_to_lower", &[Value::Char('a')]).unwrap(), Value::Char('a'));
}

#[test]
fn char_to_upper_ascii_only() {
    let c = ctx();
    assert_eq!(exec_builtin(&c, "__char_to_upper", &[Value::Char('a')]).unwrap(), Value::Char('A'));
    assert_eq!(exec_builtin(&c, "__char_to_upper", &[Value::Char('z')]).unwrap(), Value::Char('Z'));
    assert_eq!(exec_builtin(&c, "__char_to_upper", &[Value::Char('!')]).unwrap(), Value::Char('!'));
}

// ── dispatch table coverage ───────────────────────────────────────────────────

#[test]
fn unknown_builtin_errors() {
    let c = ctx();
    assert!(exec_builtin(&c, "__nonexistent", &[]).is_err());
}

#[test]
fn println_via_dispatch_table() {
    let c = ctx();
    assert!(exec_builtin(&c, "__println", &[s("test")]).is_ok());
}

// ── __obj_get_type ────────────────────────────────────────────────────────────

#[test]
fn obj_get_type_returns_type_object() {
    let c = ctx();
    let result = exec_builtin(&c, "__obj_get_type", &[obj(&c, "Foo")]).unwrap();
    match result {
        Value::Object(rc) => assert_eq!(rc.borrow().type_desc.name, "Std.Type"),
        other => panic!("expected Object, got {:?}", other),
    }
}

#[test]
fn obj_get_type_simple_name_no_namespace() {
    let c = ctx();
    let result = exec_builtin(&c, "__obj_get_type", &[obj(&c, "Foo")]).unwrap();
    let Value::Object(rc) = result else { panic!("expected Object") };
    let borrow = rc.borrow();
    assert_eq!(borrow.slots[0], Value::Str("Foo".into()));
    assert_eq!(borrow.slots[1], Value::Str("Foo".into()));
}

#[test]
fn obj_get_type_namespaced_class_splits_name() {
    let c = ctx();
    let result = exec_builtin(&c, "__obj_get_type", &[obj(&c, "geometry.Circle")]).unwrap();
    let Value::Object(rc) = result else { panic!("expected Object") };
    let borrow = rc.borrow();
    assert_eq!(borrow.slots[0], Value::Str("Circle".into()));
    assert_eq!(borrow.slots[1], Value::Str("geometry.Circle".into()));
}

#[test]
fn obj_get_type_null_errors() {
    let c = ctx();
    assert!(exec_builtin(&c, "__obj_get_type", &[Value::Null]).is_err());
}

#[test]
fn obj_get_type_non_object_errors() {
    let c = ctx();
    assert!(exec_builtin(&c, "__obj_get_type", &[i(42)]).is_err());
}

// ── __obj_ref_eq ──────────────────────────────────────────────────────────────

#[test]
fn obj_ref_eq_same_rc_is_true() {
    let c = ctx();
    let a = obj(&c, "Foo");
    assert_eq!(
        exec_builtin(&c, "__obj_ref_eq", &[a.clone(), a]).unwrap(),
        Value::Bool(true)
    );
}

#[test]
fn obj_ref_eq_different_allocs_is_false() {
    let c = ctx();
    assert_eq!(
        exec_builtin(&c, "__obj_ref_eq", &[obj(&c, "Foo"), obj(&c, "Foo")]).unwrap(),
        Value::Bool(false)
    );
}

#[test]
fn obj_ref_eq_both_null_is_true() {
    let c = ctx();
    assert_eq!(
        exec_builtin(&c, "__obj_ref_eq", &[Value::Null, Value::Null]).unwrap(),
        Value::Bool(true)
    );
}

#[test]
fn obj_ref_eq_one_null_is_false() {
    let c = ctx();
    assert_eq!(
        exec_builtin(&c, "__obj_ref_eq", &[obj(&c, "Foo"), Value::Null]).unwrap(),
        Value::Bool(false)
    );
    assert_eq!(
        exec_builtin(&c, "__obj_ref_eq", &[Value::Null, obj(&c, "Foo")]).unwrap(),
        Value::Bool(false)
    );
}

// ── __obj_hash_code ───────────────────────────────────────────────────────────

#[test]
fn obj_hash_code_returns_i32() {
    let c = ctx();
    let result = exec_builtin(&c, "__obj_hash_code", &[obj(&c, "Foo")]).unwrap();
    assert!(matches!(result, Value::I64(_)));
}

#[test]
fn obj_hash_code_same_object_is_consistent() {
    let c = ctx();
    let a = obj(&c, "Foo");
    let h1 = exec_builtin(&c, "__obj_hash_code", &[a.clone()]).unwrap();
    let h2 = exec_builtin(&c, "__obj_hash_code", &[a]).unwrap();
    assert_eq!(h1, h2);
}

#[test]
fn obj_hash_code_null_is_zero() {
    let c = ctx();
    assert_eq!(
        exec_builtin(&c, "__obj_hash_code", &[Value::Null]).unwrap(),
        Value::I64(0)
    );
}

#[test]
fn obj_hash_code_non_object_errors() {
    let c = ctx();
    assert!(exec_builtin(&c, "__obj_hash_code", &[i(1)]).is_err());
}

// ── __delegate_eq (2026-05-03 fix-delegate-reference-equality, D-5) ───────────

use crate::gc::GcRef;

fn fn_ref(name: &str) -> Value { Value::FuncRef(name.into()) }

#[test]
fn delegate_eq_same_funcref_equal() {
    let c = ctx();
    let a = fn_ref("Demo.Helper");
    let b = fn_ref("Demo.Helper");
    assert_eq!(exec_builtin(&c, "__delegate_eq", &[a, b]).unwrap(), Value::Bool(true));
}

#[test]
fn delegate_eq_diff_funcref_not_equal() {
    let c = ctx();
    let a = fn_ref("Demo.A");
    let b = fn_ref("Demo.B");
    assert_eq!(exec_builtin(&c, "__delegate_eq", &[a, b]).unwrap(), Value::Bool(false));
}

#[test]
fn delegate_eq_same_closure_equal_via_ptr_eq() {
    let c = ctx();
    let env = GcRef::new(vec![Value::I64(1)]);
    let a = Value::Closure { env: env.clone(), fn_name: "Demo.Lambda".into() };
    let b = Value::Closure { env: env.clone(), fn_name: "Demo.Lambda".into() };
    assert_eq!(exec_builtin(&c, "__delegate_eq", &[a, b]).unwrap(), Value::Bool(true));
}

#[test]
fn delegate_eq_diff_closure_env_not_equal() {
    let c = ctx();
    let a = Value::Closure { env: GcRef::new(vec![Value::I64(1)]), fn_name: "Demo.Lambda".into() };
    let b = Value::Closure { env: GcRef::new(vec![Value::I64(1)]), fn_name: "Demo.Lambda".into() };
    assert_eq!(exec_builtin(&c, "__delegate_eq", &[a, b]).unwrap(), Value::Bool(false));
}

#[test]
fn delegate_eq_same_stackclosure_equal() {
    let c = ctx();
    let a = Value::StackClosure { env_idx: 0, fn_name: "Demo.Stack".into() };
    let b = Value::StackClosure { env_idx: 0, fn_name: "Demo.Stack".into() };
    assert_eq!(exec_builtin(&c, "__delegate_eq", &[a, b]).unwrap(), Value::Bool(true));
}

#[test]
fn delegate_eq_diff_stackclosure_idx_not_equal() {
    let c = ctx();
    let a = Value::StackClosure { env_idx: 0, fn_name: "Demo.Stack".into() };
    let b = Value::StackClosure { env_idx: 1, fn_name: "Demo.Stack".into() };
    assert_eq!(exec_builtin(&c, "__delegate_eq", &[a, b]).unwrap(), Value::Bool(false));
}

#[test]
fn delegate_eq_funcref_vs_closure_not_equal() {
    let c = ctx();
    let a = fn_ref("Demo.F");
    let b = Value::Closure { env: GcRef::new(vec![]), fn_name: "Demo.F".into() };
    assert_eq!(exec_builtin(&c, "__delegate_eq", &[a, b]).unwrap(), Value::Bool(false));
}

#[test]
fn delegate_eq_closure_vs_stackclosure_not_equal() {
    let c = ctx();
    let a = Value::Closure { env: GcRef::new(vec![]), fn_name: "Demo.F".into() };
    let b = Value::StackClosure { env_idx: 0, fn_name: "Demo.F".into() };
    assert_eq!(exec_builtin(&c, "__delegate_eq", &[a, b]).unwrap(), Value::Bool(false));
}

#[test]
fn delegate_eq_both_null_equal() {
    let c = ctx();
    assert_eq!(exec_builtin(&c, "__delegate_eq", &[Value::Null, Value::Null]).unwrap(), Value::Bool(true));
}

#[test]
fn delegate_eq_null_vs_funcref_not_equal() {
    let c = ctx();
    assert_eq!(
        exec_builtin(&c, "__delegate_eq", &[Value::Null, fn_ref("Demo.F")]).unwrap(),
        Value::Bool(false)
    );
}

#[test]
fn delegate_eq_non_delegate_values_returns_false() {
    let c = ctx();
    assert_eq!(exec_builtin(&c, "__delegate_eq", &[i(5), s("foo")]).unwrap(), Value::Bool(false));
    assert_eq!(exec_builtin(&c, "__delegate_eq", &[obj(&c, "Foo"), i(0)]).unwrap(), Value::Bool(false));
}
