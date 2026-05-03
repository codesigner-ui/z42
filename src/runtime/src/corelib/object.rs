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
            crate::metadata::FieldSlot { name: "__name".to_string(), type_tag: "str".to_string() },
            crate::metadata::FieldSlot { name: "__fullName".to_string(), type_tag: "str".to_string() },
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
        (Some(Value::Object(a)), Some(Value::Object(b))) => crate::gc::GcRef::ptr_eq(a, b),
        (Some(Value::Null), Some(Value::Null))           => true,
        (Some(Value::Null), _) | (_, Some(Value::Null)) => false,
        _                                                => false,
    };
    Ok(Value::Bool(result))
}

/// 2026-05-03 fix-delegate-reference-equality (D-5)：delegate reference
/// equality —— 三个 `Value` 变体（FuncRef / Closure / StackClosure）按
/// 各自身份语义比较。跨变体不等，非 delegate 值返回 false 不报错。
///
/// 语义参见 `delegates-events.md` 与本 spec design.md：
/// - `FuncRef(name)` —— fn name 字符串相等
/// - `Closure { env, fn_name }` —— fn_name 相等且 env GcRef::ptr_eq
/// - `StackClosure { env_idx, fn_name }` —— fn_name 相等且 env_idx 相等
pub fn builtin_delegate_eq(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let result = match (args.first(), args.get(1)) {
        (Some(Value::FuncRef(a)), Some(Value::FuncRef(b))) => a == b,
        (
            Some(Value::Closure { env: ea, fn_name: na }),
            Some(Value::Closure { env: eb, fn_name: nb }),
        ) => na == nb && crate::gc::GcRef::ptr_eq(ea, eb),
        (
            Some(Value::StackClosure { env_idx: ia, fn_name: na }),
            Some(Value::StackClosure { env_idx: ib, fn_name: nb }),
        ) => na == nb && ia == ib,
        (Some(Value::Null), Some(Value::Null))           => true,
        (Some(Value::Null), _) | (_, Some(Value::Null)) => false,
        _                                                => false,
    };
    Ok(Value::Bool(result))
}

/// Identity-based hash code derived from the Rc pointer address.
pub fn builtin_obj_hash_code(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Object(gc)) => {
            let addr = crate::gc::GcRef::as_ptr(gc) as *const _ as i64;
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
        (Some(Value::Object(a)), Some(Value::Object(b))) => crate::gc::GcRef::ptr_eq(a, b),
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
