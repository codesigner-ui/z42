//! Unit tests for reflection builtins. These cover the lenient / no-handle
//! paths that don't require z42.core to be loaded. End-to-end enumeration of
//! real fields/methods into populated `FieldInfo`/`MethodInfo` objects is
//! covered by the z42 `[Test]` + golden tests (they run with z42.core present).

use super::*;
use crate::metadata::{tokens::TypeId, NameIndex, NativeData, TypeDesc, Value};
use crate::vm_context::VmContext;
use std::sync::Arc;

fn ctx() -> std::pin::Pin<Box<VmContext>> {
    VmContext::new()
}

fn bare_td(name: &str) -> Arc<TypeDesc> {
    Arc::new(TypeDesc {
        name: name.to_string(),
        base_name: None,
        fields: Vec::new(),
        field_index: NameIndex::new(),
        vtable: Vec::new(),
        vtable_index: NameIndex::new(),
        cold: None,
        id: TypeId::UNRESOLVED,
    })
}

/// A `Std.Type`-like object carrying a `TypeHandle` to `td`.
fn type_obj(ctx: &VmContext, td: Arc<TypeDesc>) -> Value {
    let shell = bare_td("Std.Type");
    ctx.heap()
        .alloc_object(shell, Vec::new(), NativeData::TypeHandle(td))
}

fn array_len(v: &Value) -> usize {
    match v {
        Value::Array(a) => a.borrow().len(),
        _ => panic!("expected Value::Array"),
    }
}

#[test]
fn type_handle_extracts_arc_for_typehandle_object() {
    let c = ctx();
    let t = type_obj(&c, bare_td("Demo.Point"));
    assert!(type_handle(&[t]).is_some());
}

#[test]
fn type_handle_none_for_primitive_plain_object_and_empty() {
    let c = ctx();
    assert!(type_handle(&[Value::I64(1)]).is_none());
    let plain = c
        .heap()
        .alloc_object(bare_td("Demo.X"), Vec::new(), NativeData::None);
    assert!(type_handle(&[plain]).is_none());
    assert!(type_handle(&[]).is_none());
}

#[test]
fn member_builtins_return_empty_for_non_type_arg() {
    let c = ctx();
    assert_eq!(array_len(&builtin_type_fields(&c, &[Value::I64(7)]).unwrap()), 0);
    assert_eq!(array_len(&builtin_type_methods(&c, &[Value::I64(7)]).unwrap()), 0);
    assert_eq!(
        array_len(&builtin_type_generic_args(&c, &[Value::I64(7)]).unwrap()),
        0
    );
}

#[test]
fn type_base_null_for_non_type_arg() {
    let c = ctx();
    assert_eq!(builtin_type_base(&c, &[Value::I64(7)]).unwrap(), Value::Null);
}

#[test]
fn type_fields_empty_for_handle_with_no_fields() {
    // TypeHandle present but zero fields → empty array, and crucially no
    // FieldInfo class lookup (so it works without z42.core loaded).
    let c = ctx();
    let t = type_obj(&c, bare_td("Demo.Empty"));
    assert_eq!(array_len(&builtin_type_fields(&c, &[t]).unwrap()), 0);
}

#[test]
fn member_builtins_are_lenient_with_handle_but_no_core() {
    // With a handle but z42.core absent, methods/base/generics must not error
    // (they degrade — base resolves to a null Std.Type, etc.).
    let c = ctx();
    let t = type_obj(&c, bare_td("Demo.Foo"));
    assert!(builtin_type_methods(&c, &[t.clone()]).is_ok());
    assert!(builtin_type_generic_args(&c, &[t.clone()]).is_ok());
    assert!(builtin_type_base(&c, &[t]).is_ok());
}
