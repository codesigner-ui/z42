use crate::metadata::{ObjectData, Value};
use anyhow::{bail, Result};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;

// ── StringBuilder pseudo-class builtins ───────────────────────────────────────
//
// `StringBuilder` is handled as a pseudo-class (like List/Dict): IrGen intercepts
// `new StringBuilder()` and routes it to `__sb_new`.  All method calls resolve
// to `Std.Text.StringBuilder.*` functions (native-backed) via the stdlib instance
// index, bypassing the List/Dict pseudo-class table.
//
// The underlying value is a `Value::Object` whose `class_name` is
// `"Std.Text.StringBuilder"` and whose only field is `__data: Value::Str`.
// Using `Rc<RefCell<ObjectData>>` gives reference semantics and in-place
// mutation for O(1) amortised `push_str`.

fn sb_data_mut(args: &[Value], builtin: &str) -> Result<Rc<RefCell<ObjectData>>> {
    match args.first() {
        Some(Value::Object(rc)) if rc.borrow().class_name == "Std.Text.StringBuilder" => {
            Ok(rc.clone())
        }
        _ => bail!("{}: expected StringBuilder object as first argument", builtin),
    }
}

/// `new StringBuilder()` → creates a fresh StringBuilder object with an empty buffer.
/// args: []
pub fn builtin_sb_new(_args: &[Value]) -> Result<Value> {
    let mut fields = HashMap::new();
    fields.insert("__data".to_string(), Value::Str(String::new()));
    Ok(Value::Object(Rc::new(RefCell::new(ObjectData {
        class_name: "Std.Text.StringBuilder".to_string(),
        fields,
    }))))
}

/// Appends `value` to the buffer.  Returns `this` for chaining.
/// args: [this, value: string]
pub fn builtin_sb_append(args: &[Value]) -> Result<Value> {
    let rc = sb_data_mut(args, "__sb_append")?;
    let value = match args.get(1) {
        Some(Value::Str(s)) => s.clone(),
        Some(other) => format!("{other:?}"),
        None => bail!("__sb_append: missing value argument"),
    };
    {
        let mut borrowed = rc.borrow_mut();
        match borrowed.fields.get_mut("__data") {
            Some(Value::Str(s)) => s.push_str(&value),
            _ => bail!("__sb_append: StringBuilder.__data corrupted"),
        }
    }
    Ok(Value::Object(rc))
}

/// Appends `value` followed by a newline.  Returns `this` for chaining.
/// args: [this, value: string]
pub fn builtin_sb_append_line(args: &[Value]) -> Result<Value> {
    let rc = sb_data_mut(args, "__sb_append_line")?;
    let value = match args.get(1) {
        Some(Value::Str(s)) => s.clone(),
        Some(other) => format!("{other:?}"),
        None => bail!("__sb_append_line: missing value argument"),
    };
    {
        let mut borrowed = rc.borrow_mut();
        match borrowed.fields.get_mut("__data") {
            Some(Value::Str(s)) => { s.push_str(&value); s.push('\n'); }
            _ => bail!("__sb_append_line: StringBuilder.__data corrupted"),
        }
    }
    Ok(Value::Object(rc))
}

/// Appends only a newline.  Returns `this` for chaining.
/// args: [this]
pub fn builtin_sb_append_newline(args: &[Value]) -> Result<Value> {
    let rc = sb_data_mut(args, "__sb_append_newline")?;
    {
        let mut borrowed = rc.borrow_mut();
        match borrowed.fields.get_mut("__data") {
            Some(Value::Str(s)) => s.push('\n'),
            _ => bail!("__sb_append_newline: StringBuilder.__data corrupted"),
        }
    }
    Ok(Value::Object(rc))
}

/// Returns the number of characters in the buffer.
/// args: [this]
pub fn builtin_sb_length(args: &[Value]) -> Result<Value> {
    let rc = sb_data_mut(args, "__sb_length")?;
    let len = match rc.borrow().fields.get("__data") {
        Some(Value::Str(s)) => s.len() as i32,
        _ => bail!("__sb_length: StringBuilder.__data corrupted"),
    };
    Ok(Value::I32(len))
}

/// Returns the accumulated string.
/// args: [this]
pub fn builtin_sb_to_string(args: &[Value]) -> Result<Value> {
    let rc = sb_data_mut(args, "__sb_to_string")?;
    let s = match rc.borrow().fields.get("__data") {
        Some(Value::Str(s)) => s.clone(),
        _ => bail!("__sb_to_string: StringBuilder.__data corrupted"),
    };
    Ok(Value::Str(s))
}
