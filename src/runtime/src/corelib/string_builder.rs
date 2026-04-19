use crate::metadata::{NativeData, ScriptObject, TypeDesc, Value};
use anyhow::{bail, Result};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::Arc;

// ── StringBuilder pseudo-class builtins ───────────────────────────────────────
//
// `StringBuilder` is handled as a pseudo-class: IrGen intercepts
// `new StringBuilder()` and routes it to `__sb_new`.  All method calls
// resolve to `Std.Text.StringBuilder.*` via the stdlib instance index.
//
// The backing store is `ScriptObject.native = NativeData::StringBuilder(String)`.
// This follows CoreCLR's pattern of native-backed special types: the buffer is
// internal and not accessible as a z42 field.

/// Extract the mutable `Rc<RefCell<ScriptObject>>` for a StringBuilder argument.
fn sb_object(args: &[Value], builtin: &str) -> Result<Rc<RefCell<ScriptObject>>> {
    match args.first() {
        Some(Value::Object(rc))
            if matches!(rc.borrow().native, NativeData::StringBuilder(_)) =>
        {
            Ok(rc.clone())
        }
        _ => bail!("{}: expected StringBuilder object as first argument", builtin),
    }
}

/// Minimal TypeDesc for StringBuilder — used when the type registry is absent.
fn sb_type_desc() -> Arc<TypeDesc> {
    Arc::new(TypeDesc {
        name: "Std.Text.StringBuilder".to_string(),
        base_name: None,
        fields: Vec::new(),
        field_index: HashMap::new(),
        vtable: Vec::new(),
        vtable_index: HashMap::new(),
    })
}

/// `new StringBuilder()` — creates a fresh StringBuilder with an empty native buffer.
/// args: []
pub fn builtin_sb_new(_args: &[Value]) -> Result<Value> {
    Ok(Value::Object(Rc::new(RefCell::new(ScriptObject {
        type_desc: sb_type_desc(),
        slots: Vec::new(),
        native: NativeData::StringBuilder(String::new()),
    }))))
}

/// Appends `value` to the buffer.  Returns `this` for chaining.
/// args: [this, value: string]
pub fn builtin_sb_append(args: &[Value]) -> Result<Value> {
    let rc = sb_object(args, "__sb_append")?;
    let value = match args.get(1) {
        Some(Value::Str(s)) => s.clone(),
        Some(other)         => crate::corelib::convert::value_to_str(other),
        None => bail!("__sb_append: missing value argument"),
    };
    match &mut rc.borrow_mut().native {
        NativeData::StringBuilder(buf) => buf.push_str(&value),
        _ => bail!("__sb_append: native buffer corrupted"),
    }
    Ok(Value::Object(rc))
}

/// Appends `value` followed by a newline.  Returns `this` for chaining.
/// args: [this, value: string]
pub fn builtin_sb_append_line(args: &[Value]) -> Result<Value> {
    let rc = sb_object(args, "__sb_append_line")?;
    let value = match args.get(1) {
        Some(Value::Str(s)) => s.clone(),
        Some(other)         => crate::corelib::convert::value_to_str(other),
        None => bail!("__sb_append_line: missing value argument"),
    };
    match &mut rc.borrow_mut().native {
        NativeData::StringBuilder(buf) => { buf.push_str(&value); buf.push('\n'); }
        _ => bail!("__sb_append_line: native buffer corrupted"),
    }
    Ok(Value::Object(rc))
}

/// Appends only a newline.  Returns `this` for chaining.
/// args: [this]
pub fn builtin_sb_append_newline(args: &[Value]) -> Result<Value> {
    let rc = sb_object(args, "__sb_append_newline")?;
    match &mut rc.borrow_mut().native {
        NativeData::StringBuilder(buf) => buf.push('\n'),
        _ => bail!("__sb_append_newline: native buffer corrupted"),
    }
    Ok(Value::Object(rc))
}

/// Returns the number of characters in the buffer.
/// args: [this]
pub fn builtin_sb_length(args: &[Value]) -> Result<Value> {
    let rc = sb_object(args, "__sb_length")?;
    let len = match &rc.borrow().native {
        NativeData::StringBuilder(buf) => buf.chars().count() as i32,
        _ => bail!("__sb_length: native buffer corrupted"),
    };
    Ok(Value::I64(len as i64))
}

/// Returns the accumulated string.
/// args: [this]
pub fn builtin_sb_to_string(args: &[Value]) -> Result<Value> {
    let rc = sb_object(args, "__sb_to_string")?;
    let s = match &rc.borrow().native {
        NativeData::StringBuilder(buf) => buf.clone(),
        _ => bail!("__sb_to_string: native buffer corrupted"),
    };
    Ok(Value::Str(s))
}
