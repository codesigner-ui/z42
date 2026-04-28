use crate::metadata::{NativeData, TypeDesc, Value};
use crate::vm_context::VmContext;
use anyhow::{bail, Result};
use std::collections::HashMap;
use std::sync::Arc;

// ── Object protocol ───────────────────────────────────────────────────────────

/// Returns a `Std.Type` object with `__name` and `__fullName` derived from
/// the runtime class of the argument.
pub fn builtin_obj_get_type(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let class_name = match args.first() {
        Some(Value::Object(rc)) => rc.borrow().type_desc.name.clone(),
        Some(Value::Null) => bail!("__obj_get_type: null reference"),
        _ => bail!("__obj_get_type: expected an object"),
    };
    let simple_name = class_name.split('.').next_back().unwrap_or(&class_name).to_string();

    // Build a minimal Type object with __name and __fullName slots.
    let mut field_index = HashMap::new();
    field_index.insert("__name".to_string(), 0usize);
    field_index.insert("__fullName".to_string(), 1usize);
    let type_desc = Arc::new(TypeDesc {
        name: crate::metadata::well_known_names::STD_TYPE.to_string(),
        base_name: None,
        fields: vec![
            crate::metadata::FieldSlot { name: "__name".to_string() },
            crate::metadata::FieldSlot { name: "__fullName".to_string() },
        ],
        field_index,
        vtable: Vec::new(),
        vtable_index: HashMap::new(), type_params: vec![], type_args: vec![],
        type_param_constraints: vec![],
    });
    Ok(ctx.heap().alloc_object(
        type_desc,
        vec![Value::Str(simple_name), Value::Str(class_name)],
        NativeData::None,
    ))
}

/// Reference equality: true iff both arguments point to the same heap allocation,
/// or both are null.
pub fn builtin_obj_ref_eq(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let result = match (args.first(), args.get(1)) {
        (Some(Value::Object(a)), Some(Value::Object(b))) => std::rc::Rc::ptr_eq(a, b),
        (Some(Value::Null), Some(Value::Null))           => true,
        (Some(Value::Null), _) | (_, Some(Value::Null)) => false,
        _                                                => false,
    };
    Ok(Value::Bool(result))
}

/// Identity-based hash code derived from the Rc pointer address.
pub fn builtin_obj_hash_code(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Object(rc)) => {
            let addr = std::rc::Rc::as_ptr(rc) as i64;
            Ok(Value::I64((addr & 0x7fff_ffff) as i64))
        }
        Some(Value::Null) => Ok(Value::I64(0)),
        _ => bail!("__obj_hash_code: expected an object"),
    }
}

/// Value equality — defaults to reference equality.
/// args: [this, other]
pub fn builtin_obj_equals(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let result = match (args.first(), args.get(1)) {
        (Some(Value::Object(a)), Some(Value::Object(b))) => std::rc::Rc::ptr_eq(a, b),
        (Some(Value::Null), Some(Value::Null))           => true,
        (Some(Value::Null), _) | (_, Some(Value::Null)) => false,
        _                                                => false,
    };
    Ok(Value::Bool(result))
}

/// Human-readable representation — returns the unqualified type name.
/// args: [this]
pub fn builtin_obj_to_str(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Object(rc)) => {
            let class_name = rc.borrow().type_desc.name.clone();
            let simple = class_name.split('.').next_back().unwrap_or(&class_name).to_string();
            Ok(Value::Str(simple))
        }
        Some(Value::Null) => Ok(Value::Str("null".into())),
        _ => bail!("__obj_to_str: expected an object"),
    }
}

// 2026-04-27 wave1-assert-script: 6 `builtin_assert_*` functions removed.
// `Std.Assert` is now pure z42 script in `z42.core/src/Assert.z42`.
