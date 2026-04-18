/// Single-instruction dispatch for the interpreter.
///
/// Each match arm corresponds to one IR instruction. Object-related dispatch
/// helpers live in `dispatch.rs`; register-level numeric helpers in `ops.rs`.

use crate::metadata::{Instruction, Module, NativeData, ScriptObject, Value};
use anyhow::{bail, Result};
use std::cell::RefCell;
use std::rc::Rc;

use super::dispatch::{
    is_subclass_or_eq_td, make_fallback_type_desc, obj_to_string, resolve_virtual,
    static_get, static_set, value_to_str,
};
use super::ops::{bool_val, collect_args, int_binop, int_bitop, numeric_lt, str_val, to_usize};
use super::{ExecOutcome, Frame};

/// Execute a single instruction.
/// Returns:
///   Ok(None)       — normal completion
///   Ok(Some(val))  — a callee threw a user exception (value-based propagation)
///   Err(e)         — internal VM error
pub fn exec_instr(module: &Module, frame: &mut Frame, instr: &Instruction) -> Result<Option<Value>> {
    match instr {
        // ── Constants ────────────────────────────────────────────────────────
        Instruction::ConstStr { dst, idx } => {
            let i = *idx as usize;
            let s = if let Some(s) = module.string_pool.get(i) {
                s.clone()
            } else if let Some(s) = crate::metadata::lazy_loader::try_lookup_string(i) {
                // ConstStr from a lazily-loaded function — idx is offset past main pool.
                s
            } else {
                bail!("string pool index {idx} out of range");
            };
            frame.set(*dst, Value::Str(s));
        }
        Instruction::ConstI32  { dst, val } => frame.set(*dst, Value::I32(*val)),
        Instruction::ConstI64  { dst, val } => frame.set(*dst, Value::I64(*val)),
        Instruction::ConstF64  { dst, val } => frame.set(*dst, Value::F64(*val)),
        Instruction::ConstBool { dst, val } => frame.set(*dst, Value::Bool(*val)),
        Instruction::ConstChar { dst, val } => frame.set(*dst, Value::Char(*val)),
        Instruction::ConstNull { dst }      => frame.set(*dst, Value::Null),
        Instruction::Copy      { dst, src } => frame.set(*dst, frame.get(*src)?.clone()),

        // ── Arithmetic ───────────────────────────────────────────────────────
        Instruction::Add { dst, a, b } => {
            let result = match (frame.get(*a)?, frame.get(*b)?) {
                (Value::Str(sa), Value::Str(sb)) => Value::Str(format!("{}{}", sa, sb)),
                (Value::Str(sa), vb)             => Value::Str(format!("{}{}", sa, value_to_str(vb))),
                (va, Value::Str(sb))             => Value::Str(format!("{}{}", value_to_str(va), sb)),
                _                                => int_binop(&frame.regs, *a, *b, |x, y| x + y, |x, y| x + y)?,
            };
            frame.set(*dst, result);
        }
        Instruction::Sub { dst, a, b } => {
            frame.set(*dst, int_binop(&frame.regs, *a, *b, |x, y| x - y, |x, y| x - y)?);
        }
        Instruction::Mul { dst, a, b } => {
            frame.set(*dst, int_binop(&frame.regs, *a, *b, |x, y| x * y, |x, y| x * y)?);
        }
        Instruction::Div { dst, a, b } => {
            frame.set(*dst, int_binop(&frame.regs, *a, *b, |x, y| x / y, |x, y| x / y)?);
        }
        Instruction::Rem { dst, a, b } => {
            frame.set(*dst, int_binop(&frame.regs, *a, *b, |x, y| x % y, |x, y| x % y)?);
        }

        // ── Comparison ───────────────────────────────────────────────────────
        Instruction::Eq { dst, a, b } => {
            frame.set(*dst, Value::Bool(frame.get(*a)? == frame.get(*b)?));
        }
        Instruction::Ne { dst, a, b } => {
            frame.set(*dst, Value::Bool(frame.get(*a)? != frame.get(*b)?));
        }
        Instruction::Lt { dst, a, b } => {
            frame.set(*dst, Value::Bool(numeric_lt(&frame.regs, *a, *b)?));
        }
        Instruction::Le { dst, a, b } => {
            frame.set(*dst, Value::Bool(!numeric_lt(&frame.regs, *b, *a)?));
        }
        Instruction::Gt { dst, a, b } => {
            frame.set(*dst, Value::Bool(numeric_lt(&frame.regs, *b, *a)?));
        }
        Instruction::Ge { dst, a, b } => {
            frame.set(*dst, Value::Bool(!numeric_lt(&frame.regs, *a, *b)?));
        }

        // ── Logical ──────────────────────────────────────────────────────────
        Instruction::And { dst, a, b } => {
            frame.set(*dst, Value::Bool(bool_val(&frame.regs, *a)? && bool_val(&frame.regs, *b)?));
        }
        Instruction::Or { dst, a, b } => {
            frame.set(*dst, Value::Bool(bool_val(&frame.regs, *a)? || bool_val(&frame.regs, *b)?));
        }
        Instruction::Not { dst, src } => {
            frame.set(*dst, Value::Bool(!bool_val(&frame.regs, *src)?));
        }

        // ── Unary arithmetic ─────────────────────────────────────────────────
        Instruction::Neg { dst, src } => {
            let res = match frame.get(*src)? {
                Value::I32(n) => Value::I32(-n),
                Value::I64(n) => Value::I64(-n),
                Value::F64(f) => Value::F64(-f),
                other => bail!("Neg: expected numeric, got {:?}", other),
            };
            frame.set(*dst, res);
        }

        // ── Bitwise ──────────────────────────────────────────────────────────
        Instruction::BitAnd { dst, a, b } => {
            frame.set(*dst, int_bitop(&frame.regs, *a, *b, |x, y| x & y)?);
        }
        Instruction::BitOr { dst, a, b } => {
            frame.set(*dst, int_bitop(&frame.regs, *a, *b, |x, y| x | y)?);
        }
        Instruction::BitXor { dst, a, b } => {
            frame.set(*dst, int_bitop(&frame.regs, *a, *b, |x, y| x ^ y)?);
        }
        Instruction::BitNot { dst, src } => {
            let res = match frame.get(*src)? {
                Value::I32(n) => Value::I32(!n),
                Value::I64(n) => Value::I64(!n),
                other => bail!("BitNot: expected integral, got {:?}", other),
            };
            frame.set(*dst, res);
        }
        Instruction::Shl { dst, a, b } => {
            frame.set(*dst, int_bitop(&frame.regs, *a, *b, |x, y| x << (y & 63))?);
        }
        Instruction::Shr { dst, a, b } => {
            frame.set(*dst, int_bitop(&frame.regs, *a, *b, |x, y| x >> (y & 63))?);
        }

        // ── String ───────────────────────────────────────────────────────────
        Instruction::StrConcat { dst, a, b } => {
            let sa = str_val(&frame.regs, *a)?;
            let sb = str_val(&frame.regs, *b)?;
            frame.set(*dst, Value::Str(format!("{}{}", sa, sb)));
        }
        Instruction::ToStr { dst, src } => {
            let s = obj_to_string(module, frame.get(*src)?)?;
            frame.set(*dst, Value::Str(s));
        }

        // ── Calls ────────────────────────────────────────────────────────────
        Instruction::Call { dst, func: fname, args } => {
            let arg_vals = collect_args(&frame.regs, args)?;
            let callee_fn = module.functions.iter().find(|f| f.name == *fname);
            let outcome = if let Some(callee) = callee_fn {
                super::exec_function(module, callee, &arg_vals)?
            } else if let Some(lazy_fn) = crate::metadata::lazy_loader::try_lookup_function(fname) {
                super::exec_function(module, lazy_fn.as_ref(), &arg_vals)?
            } else {
                bail!("undefined function `{fname}`");
            };
            match outcome {
                ExecOutcome::Returned(ret) => frame.set(*dst, ret.unwrap_or(Value::Null)),
                ExecOutcome::Thrown(val) => return Ok(Some(val)),
            }
        }

        Instruction::Builtin { dst, name, args } => {
            let arg_vals = collect_args(&frame.regs, args)?;
            let result = crate::corelib::exec_builtin(name, &arg_vals)?;
            frame.set(*dst, result);
        }

        // ── Arrays ───────────────────────────────────────────────────────────
        Instruction::ArrayNew { dst, size } => {
            let n = to_usize(frame.get(*size)?, "ArrayNew size")?;
            frame.set(*dst, Value::Array(Rc::new(RefCell::new(vec![Value::Null; n]))));
        }
        Instruction::ArrayNewLit { dst, elems } => {
            let vals: Vec<Value> = elems.iter()
                .map(|r| frame.get(*r).map(|v| v.clone()))
                .collect::<Result<_>>()?;
            frame.set(*dst, Value::Array(Rc::new(RefCell::new(vals))));
        }
        Instruction::ArrayGet { dst, arr, idx } => {
            let result = match frame.get(*arr)? {
                Value::Array(rc) => {
                    let rc = rc.clone();
                    let i = to_usize(frame.get(*idx)?, "ArrayGet index")?;
                    let borrowed = rc.borrow();
                    if i >= borrowed.len() {
                        bail!("array index {} out of bounds (len={})", i, borrowed.len());
                    }
                    borrowed[i].clone()
                }
                Value::Map(rc) => {
                    let rc = rc.clone();
                    let key = value_to_str(frame.get(*idx)?);
                    let borrowed = rc.borrow();
                    borrowed.get(&key).cloned().unwrap_or(Value::Null)
                }
                other => bail!("ArrayGet: expected array or map, got {:?}", other),
            };
            frame.set(*dst, result);
        }
        Instruction::ArraySet { arr, idx, val } => {
            let v = frame.get(*val)?.clone();
            match frame.get(*arr)? {
                Value::Array(rc) => {
                    let rc = rc.clone();
                    let i = to_usize(frame.get(*idx)?, "ArraySet index")?;
                    let mut borrowed = rc.borrow_mut();
                    if i >= borrowed.len() {
                        bail!("array index {} out of bounds (len={})", i, borrowed.len());
                    }
                    borrowed[i] = v;
                }
                Value::Map(rc) => {
                    let rc = rc.clone();
                    let key = value_to_str(frame.get(*idx)?);
                    rc.borrow_mut().insert(key, v);
                }
                other => bail!("ArraySet: expected array or map, got {:?}", other),
            }
        }
        Instruction::ArrayLen { dst, arr } => {
            let len = match frame.get(*arr)? {
                Value::Array(rc) => rc.borrow().len() as i32,
                other => bail!("ArrayLen: expected array, got {:?}", other),
            };
            frame.set(*dst, Value::I32(len));
        }

        // ── Objects ──────────────────────────────────────────────────────────
        Instruction::ObjNew { dst, class_name, args } => {
            let type_desc = module.type_registry
                .get(class_name)
                .cloned()
                .unwrap_or_else(|| {
                    std::sync::Arc::new(make_fallback_type_desc(module, class_name))
                });

            let slots = vec![Value::Null; type_desc.fields.len()];
            let obj_rc = Rc::new(RefCell::new(ScriptObject {
                type_desc,
                slots,
                native: NativeData::None,
            }));
            let obj_val = Value::Object(obj_rc);

            let simple_name = class_name.split('.').next_back().unwrap_or(class_name.as_str());
            let ctor_name = format!("{}.{}", class_name, simple_name);
            if let Some(ctor) = module.functions.iter().find(|f| f.name == ctor_name) {
                let mut ctor_args = vec![obj_val.clone()];
                ctor_args.extend(collect_args(&frame.regs, args)?);
                super::exec_function(module, ctor, &ctor_args)?;
            }

            frame.set(*dst, obj_val);
        }

        Instruction::FieldGet { dst, obj, field_name } => {
            let val = match frame.get(*obj)? {
                Value::Object(rc) => {
                    let borrowed = rc.borrow();
                    if let Some(&slot) = borrowed.type_desc.field_index.get(field_name) {
                        borrowed.slots.get(slot).cloned().unwrap_or(Value::Null)
                    } else {
                        Value::Null
                    }
                }
                Value::Str(s) => match field_name.as_str() {
                    "Length" => Value::I64(s.chars().count() as i64),
                    other    => bail!("string has no field `{}`", other),
                },
                Value::Array(rc) => match field_name.as_str() {
                    "Length" | "Count" => Value::I64(rc.borrow().len() as i64),
                    other => bail!("array has no field `{}`", other),
                },
                Value::Map(rc) => match field_name.as_str() {
                    "Length" | "Count" => Value::I64(rc.borrow().len() as i64),
                    other => bail!("map has no field `{}`", other),
                },
                other => bail!("FieldGet: not an object or known value type, got {:?}", other),
            };
            frame.set(*dst, val);
        }

        Instruction::FieldSet { obj, field_name, val } => {
            let v = frame.get(*val)?.clone();
            match frame.get(*obj)? {
                Value::Object(rc) => {
                    let mut borrowed = rc.borrow_mut();
                    if let Some(&slot) = borrowed.type_desc.field_index.get(field_name) {
                        if slot < borrowed.slots.len() {
                            borrowed.slots[slot] = v;
                        }
                    }
                }
                other => bail!("FieldSet: expected object, got {:?}", other),
            }
        }

        Instruction::VCall { dst, obj, method, args } => {
            let obj_val = frame.get(*obj)?.clone();
            let mut extra_args = collect_args(&frame.regs, args)?;

            // Primitive types: dispatch via builtin name table.
            let primitive_builtin: Option<&'static str> = match &obj_val {
                Value::Str(_) => match method.as_str() {
                    "ToString"    => Some("__str_to_string"),
                    "Equals"      => Some("__str_equals"),
                    "GetHashCode" => Some("__str_hash_code"),
                    _ => None,
                },
                _ => None,
            };
            if let Some(builtin_name) = primitive_builtin {
                let mut call_args = vec![obj_val];
                call_args.append(&mut extra_args);
                let ret = crate::corelib::exec_builtin(builtin_name, &call_args)?;
                frame.set(*dst, ret);
                return Ok(None);
            }

            // O(1) vtable dispatch using pre-computed TypeDesc.
            let type_desc = match &obj_val {
                Value::Object(rc) => rc.borrow().type_desc.clone(),
                other => bail!("VCall: expected object, got {:?}", other),
            };
            // Compute qualified name: try vtable first; fall back to
            // `${class}.${method}` direct composition (lazy-loaded deps may
            // lack a populated vtable in their TypeDesc stub).
            let func_name = if let Some(&slot) = type_desc.vtable_index.get(method.as_str()) {
                type_desc.vtable[slot].1.clone()
            } else if let Ok(f) = resolve_virtual(module, &type_desc.name, method) {
                f.name.clone()
            } else {
                format!("{}.{}", type_desc.name, method)
            };
            let mut call_args = vec![obj_val];
            call_args.append(&mut extra_args);
            let outcome = if let Some(callee) = module.functions.iter().find(|f| f.name == func_name) {
                super::exec_function(module, callee, &call_args)?
            } else if let Some(lazy_fn) = crate::metadata::lazy_loader::try_lookup_function(&func_name) {
                super::exec_function(module, lazy_fn.as_ref(), &call_args)?
            } else {
                bail!("VCall: function `{}` not found", func_name);
            };
            match outcome {
                ExecOutcome::Returned(ret) => frame.set(*dst, ret.unwrap_or(Value::Null)),
                ExecOutcome::Thrown(val) => return Ok(Some(val)),
            }
        }

        Instruction::IsInstance { dst, obj, class_name } => {
            let result = match frame.get(*obj)? {
                Value::Object(rc) => {
                    let runtime_class = rc.borrow().type_desc.name.clone();
                    is_subclass_or_eq_td(&module.type_registry, &runtime_class, class_name)
                }
                Value::Null => false,
                _ => false,
            };
            frame.set(*dst, Value::Bool(result));
        }

        Instruction::AsCast { dst, obj, class_name } => {
            let val = frame.get(*obj)?.clone();
            let is_match = match &val {
                Value::Object(rc) => {
                    let runtime_class = rc.borrow().type_desc.name.clone();
                    is_subclass_or_eq_td(&module.type_registry, &runtime_class, class_name)
                }
                Value::Null => true,
                _ => false,
            };
            frame.set(*dst, if is_match { val } else { Value::Null });
        }

        Instruction::StaticGet { dst, field } => {
            frame.set(*dst, static_get(field));
        }
        Instruction::StaticSet { field, val } => {
            let v = frame.get(*val)?.clone();
            static_set(field, v);
        }
    }
    Ok(None)
}
