use crate::metadata::Value;
use anyhow::{bail, Result};
use super::convert::value_to_str;

// ── List ──────────────────────────────────────────────────────────────────────

pub fn builtin_list_new(_args: &[Value]) -> Result<Value> {
    Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(vec![]))))
}
pub fn builtin_list_add(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(arr)) => {
            arr.borrow_mut().push(args.get(1).cloned().unwrap_or(Value::Null));
            Ok(Value::Null)
        }
        _ => bail!("List.Add: first argument must be a List"),
    }
}
pub fn builtin_list_remove_at(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::Array(arr)), Some(Value::I64(idx))) => {
            let idx = *idx as usize;
            let mut v = arr.borrow_mut();
            if idx >= v.len() { bail!("List.RemoveAt: index {} out of range (len={})", idx, v.len()); }
            v.remove(idx);
            Ok(Value::Null)
        }
        _ => bail!("List.RemoveAt: expected (List, i64)"),
    }
}
pub fn builtin_list_contains(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(arr)) => {
            let item = args.get(1).cloned().unwrap_or(Value::Null);
            Ok(Value::Bool(arr.borrow().iter().any(|v| v == &item)))
        }
        _ => bail!("List.Contains: first argument must be a List"),
    }
}
pub fn builtin_list_clear(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(arr)) => { arr.borrow_mut().clear(); Ok(Value::Null) }
        _ => bail!("List.Clear: first argument must be a List"),
    }
}
pub fn builtin_list_insert(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1), args.get(2)) {
        (Some(Value::Array(arr)), Some(Value::I64(idx)), Some(item)) => {
            let idx  = *idx as usize;
            let item = item.clone();
            let mut v = arr.borrow_mut();
            if idx > v.len() { bail!("List.Insert: index {} out of range (len={})", idx, v.len()); }
            v.insert(idx, item);
            Ok(Value::Null)
        }
        _ => bail!("List.Insert: expected (List, i64, value)"),
    }
}
pub fn builtin_list_sort(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(arr)) => {
            arr.borrow_mut().sort_by(|a, b| match (a, b) {
                (Value::I64(x), Value::I64(y)) => x.cmp(y),
                (Value::F64(x), Value::F64(y)) => x.partial_cmp(y).unwrap_or(std::cmp::Ordering::Equal),
                (Value::Str(x), Value::Str(y)) => x.cmp(y),
                _ => std::cmp::Ordering::Equal,
            });
            Ok(Value::Null)
        }
        _ => bail!("List.Sort: first argument must be a List"),
    }
}
pub fn builtin_list_reverse(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Array(arr)) => { arr.borrow_mut().reverse(); Ok(Value::Null) }
        _ => bail!("List.Reverse: first argument must be a List"),
    }
}

// ── Dictionary ────────────────────────────────────────────────────────────────

pub fn builtin_dict_new(_args: &[Value]) -> Result<Value> {
    Ok(Value::Map(std::rc::Rc::new(std::cell::RefCell::new(std::collections::HashMap::new()))))
}
pub fn builtin_dict_contains_key(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::Map(rc)), Some(key)) =>
            Ok(Value::Bool(rc.borrow().contains_key(&value_to_str(key)))),
        _ => bail!("Dictionary.ContainsKey: expected (Dictionary, key)"),
    }
}
pub fn builtin_dict_remove(args: &[Value]) -> Result<Value> {
    match (args.first(), args.get(1)) {
        (Some(Value::Array(arr)), Some(item)) => {
            let mut v = arr.borrow_mut();
            let removed = v.iter().position(|x| x == item).map(|p| { v.remove(p); true }).unwrap_or(false);
            Ok(Value::Bool(removed))
        }
        (Some(Value::Map(rc)), Some(key)) => {
            Ok(Value::Bool(rc.borrow_mut().remove(&value_to_str(key)).is_some()))
        }
        _ => bail!("Remove: expected (List, value) or (Dictionary, key)"),
    }
}
pub fn builtin_dict_keys(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Map(rc)) => {
            let keys: Vec<Value> = rc.borrow().keys().map(|k| Value::Str(k.clone())).collect();
            Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(keys))))
        }
        _ => bail!("Dictionary.Keys: expected Dictionary"),
    }
}
pub fn builtin_dict_values(args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Map(rc)) => {
            let vals: Vec<Value> = rc.borrow().values().cloned().collect();
            Ok(Value::Array(std::rc::Rc::new(std::cell::RefCell::new(vals))))
        }
        _ => bail!("Dictionary.Values: expected Dictionary"),
    }
}
