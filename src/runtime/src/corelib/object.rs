use crate::metadata::{ObjectData, Value};
use anyhow::{bail, Result};
use super::convert::{require_str, value_to_str};

// ── Object protocol ───────────────────────────────────────────────────────────

/// Returns a `z42.core.Type` object whose `__name` and `__fullName` fields are
/// derived from the runtime class tag of the argument object.
pub fn builtin_obj_get_type(args: &[Value]) -> Result<Value> {
    let class_name = match args.first() {
        Some(Value::Object(rc)) => rc.borrow().class_name.clone(),
        Some(Value::Null) => bail!("__obj_get_type: null reference"),
        _ => bail!("__obj_get_type: expected an object"),
    };
    let simple_name = class_name.split('.').next_back().unwrap_or(&class_name).to_string();
    let mut fields = std::collections::HashMap::new();
    fields.insert("__name".to_string(),     Value::Str(simple_name));
    fields.insert("__fullName".to_string(), Value::Str(class_name));
    Ok(Value::Object(std::rc::Rc::new(std::cell::RefCell::new(ObjectData {
        class_name: "Std.Type".to_string(),
        fields,
    }))))
}

/// Reference equality: true iff both arguments point to the same heap allocation,
/// or both are null.
pub fn builtin_obj_ref_eq(args: &[Value]) -> Result<Value> {
    let result = match (args.first(), args.get(1)) {
        (Some(Value::Object(a)), Some(Value::Object(b))) => std::rc::Rc::ptr_eq(a, b),
        (Some(Value::Null), Some(Value::Null))           => true,
        (Some(Value::Null), _) | (_, Some(Value::Null)) => false,
        _                                                => false,
    };
    Ok(Value::Bool(result))
}

/// Identity-based hash code derived from the Rc pointer address.
pub fn builtin_obj_hash_code(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Object(rc)) => {
            let addr = std::rc::Rc::as_ptr(rc) as i64;
            Ok(Value::I32((addr & 0x7fff_ffff) as i32))
        }
        Some(Value::Null) => Ok(Value::I32(0)),
        _ => bail!("__obj_hash_code: expected an object"),
    }
}

/// Value equality — defaults to reference equality (same as __obj_ref_eq).
/// args: [this, other]
pub fn builtin_obj_equals(args: &[Value]) -> Result<Value> {
    let result = match (args.first(), args.get(1)) {
        (Some(Value::Object(a)), Some(Value::Object(b))) => std::rc::Rc::ptr_eq(a, b),
        (Some(Value::Null), Some(Value::Null))           => true,
        (Some(Value::Null), _) | (_, Some(Value::Null)) => false,
        _                                                => false,
    };
    Ok(Value::Bool(result))
}

/// Human-readable representation — defaults to the unqualified type name.
/// args: [this]
pub fn builtin_obj_to_str(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Object(rc)) => {
            let class_name = rc.borrow().class_name.clone();
            let simple = class_name.split('.').next_back().unwrap_or(&class_name).to_string();
            Ok(Value::Str(simple))
        }
        Some(Value::Null) => Ok(Value::Str("null".into())),
        _ => bail!("__obj_to_str: expected an object"),
    }
}

// ── Assert ────────────────────────────────────────────────────────────────────

pub fn builtin_assert_eq(args: &[Value]) -> Result<Value> {
    let expected = args.first().cloned().unwrap_or(Value::Null);
    let actual   = args.get(1).cloned().unwrap_or(Value::Null);
    if expected != actual {
        bail!("AssertionError: expected {} but got {}",
            value_to_str(&expected), value_to_str(&actual));
    }
    Ok(Value::Null)
}
pub fn builtin_assert_true(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Bool(true)) => Ok(Value::Null),
        Some(other) => bail!("AssertionError: expected true but got {}", value_to_str(other)),
        None        => bail!("AssertionError: __assert_true missing argument"),
    }
}
pub fn builtin_assert_false(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Bool(false)) => Ok(Value::Null),
        Some(other) => bail!("AssertionError: expected false but got {}", value_to_str(other)),
        None        => bail!("AssertionError: __assert_false missing argument"),
    }
}
pub fn builtin_assert_null(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Null) | None => Ok(Value::Null),
        Some(other) => bail!("AssertionError: expected null but got {}", value_to_str(other)),
    }
}
pub fn builtin_assert_not_null(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Null) | None => bail!("AssertionError: expected non-null but got null"),
        Some(_) => Ok(Value::Null),
    }
}
pub fn builtin_assert_contains(args: &[Value]) -> Result<Value> {
    let sub = require_str(args, 0, "__assert_contains")?;
    let s   = require_str(args, 1, "__assert_contains")?;
    if !s.contains(sub.as_str()) {
        bail!("AssertionError: expected {:?} to contain {:?}", s, sub);
    }
    Ok(Value::Null)
}
