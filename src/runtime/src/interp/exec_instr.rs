/// Single-instruction dispatch for the interpreter.
///
/// Each match arm corresponds to one IR instruction. Object-related dispatch
/// helpers live in `dispatch.rs`; register-level numeric helpers in `ops.rs`.

use crate::metadata::{Instruction, Module, NativeData, Value};
use crate::vm_context::VmContext;
use anyhow::{bail, Result};

use super::dispatch::{
    is_subclass_or_eq_td, make_fallback_type_desc, obj_to_string, resolve_virtual, value_to_str,
};
use super::ops::{bool_val, collect_args, int_binop, int_bitop, numeric_lt, str_val, to_usize};
use super::{ExecOutcome, Frame};

/// L3-G4b primitive-as-struct: maps a primitive `Value` variant to its stdlib
/// struct's qualified class name (e.g. `Value::I64` → `"Std.int"`). The VM
/// dispatches primitive method calls by constructing `{class}.{method}` and
/// looking up the function in `module.func_index` — replacing the old
/// hardcoded `(Value, method)` → builtin-name switch.
///
/// Returns None for non-primitive values (objects, arrays, null, etc.).
pub(crate) fn primitive_class_name(obj: &Value) -> Option<&'static str> {
    use crate::metadata::well_known_names::*;
    match obj {
        Value::I64(_)  => Some(STD_INT),
        Value::F64(_)  => Some(STD_DOUBLE),
        Value::Bool(_) => Some(STD_BOOL),
        Value::Char(_) => Some(STD_CHAR),
        Value::Str(_)  => Some(STD_STRING),  // capitalised — stdlib retains `class String`
        _ => None,
    }
}

/// Execute a single instruction.
/// Returns:
///   Ok(None)       — normal completion
///   Ok(Some(val))  — a callee threw a user exception (value-based propagation)
///   Err(e)         — internal VM error
pub fn exec_instr(ctx: &VmContext, module: &Module, frame: &mut Frame, instr: &Instruction) -> Result<Option<Value>> {
    match instr {
        // ── Constants ────────────────────────────────────────────────────────
        Instruction::ConstStr { dst, idx } => {
            let i = *idx as usize;
            let s = if let Some(s) = module.string_pool.get(i) {
                s.clone()
            } else if let Some(s) = ctx.try_lookup_string(i) {
                // ConstStr from a lazily-loaded function — idx is offset past main pool.
                s
            } else {
                bail!("string pool index {idx} out of range");
            };
            frame.set(*dst, Value::Str(s));
        }
        Instruction::ConstI32  { dst, val } => frame.set(*dst, Value::I64(*val as i64)),
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
                // 2026-04-28 vm-wrapping-int-arith: wrapping_add（与 Rust release build /
                // C# unchecked int / Java int 一致），解锁 hash / PRNG / 校验和算法
                _ => int_binop(&frame.regs, *a, *b, i64::wrapping_add, |x, y| x + y)?,
            };
            frame.set(*dst, result);
        }
        Instruction::Sub { dst, a, b } => {
            frame.set(*dst, int_binop(&frame.regs, *a, *b, i64::wrapping_sub, |x, y| x - y)?);
        }
        Instruction::Mul { dst, a, b } => {
            frame.set(*dst, int_binop(&frame.regs, *a, *b, i64::wrapping_mul, |x, y| x * y)?);
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
            let s = obj_to_string(ctx, module, frame.get(*src)?)?;
            frame.set(*dst, Value::Str(s));
        }

        // ── Calls ────────────────────────────────────────────────────────────
        Instruction::Call { dst, func: fname, args } => {
            let arg_vals = collect_args(&frame.regs, args)?;
            let callee_fn = module.func_index.get(fname.as_str())
                .and_then(|&idx| module.functions.get(idx));
            let outcome = if let Some(callee) = callee_fn {
                super::exec_function(ctx, module, callee, &arg_vals)?
            } else if let Some(lazy_fn) = ctx.try_lookup_function(fname) {
                super::exec_function(ctx, module, lazy_fn.as_ref(), &arg_vals)?
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
            let result = crate::corelib::exec_builtin(ctx, name, &arg_vals)?;
            frame.set(*dst, result);
        }

        // L2 no-capture lambda lifting: push a function reference value.
        // See docs/design/closure.md §6 + ir.md.
        Instruction::LoadFn { dst, func } => {
            frame.set(*dst, Value::FuncRef(func.clone()));
        }

        // Indirect call via a `FuncRef`-typed register. See closure.md §6.
        Instruction::CallIndirect { dst, callee, args } => {
            let fname = match frame.get(*callee)? {
                Value::FuncRef(name) => name.clone(),
                other => bail!("CallIndirect: expected FuncRef, got {:?}", other),
            };
            let arg_vals = collect_args(&frame.regs, args)?;
            let callee_fn = module.func_index.get(fname.as_str())
                .and_then(|&idx| module.functions.get(idx));
            let outcome = if let Some(cfn) = callee_fn {
                super::exec_function(ctx, module, cfn, &arg_vals)?
            } else if let Some(lazy_fn) = ctx.try_lookup_function(&fname) {
                super::exec_function(ctx, module, lazy_fn.as_ref(), &arg_vals)?
            } else {
                bail!("CallIndirect: undefined function `{fname}`");
            };
            match outcome {
                ExecOutcome::Returned(ret) => frame.set(*dst, ret.unwrap_or(Value::Null)),
                ExecOutcome::Thrown(val)   => return Ok(Some(val)),
            }
        }

        // ── Arrays ───────────────────────────────────────────────────────────
        Instruction::ArrayNew { dst, size } => {
            let n = to_usize(frame.get(*size)?, "ArrayNew size")?;
            frame.set(*dst, ctx.heap().alloc_array(vec![Value::Null; n]));
        }
        Instruction::ArrayNewLit { dst, elems } => {
            let vals: Vec<Value> = elems.iter()
                .map(|r| frame.get(*r).map(|v| v.clone()))
                .collect::<Result<_>>()?;
            frame.set(*dst, ctx.heap().alloc_array(vals));
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
                other => bail!("ArrayGet: expected array, got {:?}", other),
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
                other => bail!("ArraySet: expected array, got {:?}", other),
            }
        }
        Instruction::ArrayLen { dst, arr } => {
            let len = match frame.get(*arr)? {
                Value::Array(rc) => rc.borrow().len() as i32,
                other => bail!("ArrayLen: expected array, got {:?}", other),
            };
            frame.set(*dst, Value::I64(len as i64));
        }

        // ── Objects ──────────────────────────────────────────────────────────
        Instruction::ObjNew { dst, class_name, ctor_name, args } => {
            // L3-G4d: for imported classes (e.g. Std.Collections.Stack) the TypeDesc
            // may only exist in the lazy loader until first use; probe it before
            // falling back to a blank synthetic descriptor.
            let type_desc = module.type_registry
                .get(class_name)
                .cloned()
                .or_else(|| ctx.try_lookup_type(class_name))
                .unwrap_or_else(|| {
                    std::sync::Arc::new(make_fallback_type_desc(module, class_name))
                });

            let slots = vec![Value::Null; type_desc.fields.len()];
            let obj_val = ctx.heap().alloc_object(type_desc, slots, NativeData::None);

            // 直查 ctor_name (TypeChecker 已 overload-resolve)；无名字推断。
            // L3-G4d: fall back to lazy loader when the ctor lives in a stdlib zpkg
            // (imported generic class ctor isn't in the main module's function table).
            let ctor_fn = module.func_index.get(ctor_name.as_str())
                .and_then(|&i| module.functions.get(i));
            if let Some(ctor) = ctor_fn {
                let mut ctor_args = vec![obj_val.clone()];
                ctor_args.extend(collect_args(&frame.regs, args)?);
                super::exec_function(ctx, module, ctor, &ctor_args)?;
            } else if let Some(lazy_ctor) = ctx.try_lookup_function(ctor_name) {
                let mut ctor_args = vec![obj_val.clone()];
                ctor_args.extend(collect_args(&frame.regs, args)?);
                super::exec_function(ctx, module, lazy_ctor.as_ref(), &ctor_args)?;
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
                Value::PinnedView { ptr, len, .. } => match field_name.as_str() {
                    // Spec C4 — only `ptr` / `len` are exposed; element type
                    // information (kind) stays internal.
                    "ptr" => Value::I64(*ptr as i64),
                    "len" => Value::I64(*len as i64),
                    other => bail!("Z0908: PinnedView has no field `{}` (only `ptr` / `len`)", other),
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

            // L3-G4b primitive-as-struct: primitives dispatch through their stdlib
            // struct's method (e.g. `Value::I64.CompareTo` → call `Std.int.CompareTo`
            // IR function, which contains a BuiltinInstr for `__int_compare_to`).
            // This replaces the old hardcoded `(Value, method) → builtin` table —
            // method-to-native binding is now entirely data-driven via stdlib source.
            //
            // Overload resolution: when the receiver type is statically `object`
            // (e.g. `Std.Assert.Equal(object, object)` calling `expected.Equals(actual)`),
            // the C# emit can't pick an overload at compile time; the IR carries the
            // unmangled method name `Equals`. But IrGen emits overloaded methods with
            // a `$N` arity suffix (e.g. `Std.String.Equals$1`). We retry with `$<arity>`
            // when the unmangled lookup misses — covers `Equals` (arity 1) and any
            // other overloaded primitive method without per-Value-type special cases.
            // This subsumes the legacy `Value::Str` hardcoded block (review2 §2.2).
            if let Some(class_name) = primitive_class_name(&obj_val) {
                let mut call_args = vec![obj_val.clone()];
                call_args.append(&mut extra_args);
                let arity = call_args.len() - 1; // exclude `this`
                let primary = format!("{}.{}", class_name, method);
                let overload = format!("{}.{}${}", class_name, method, arity);
                for func_name in [primary.as_str(), overload.as_str()] {
                    if let Some(&idx) = module.func_index.get(func_name) {
                        if let Some(callee) = module.functions.get(idx) {
                            let outcome = super::exec_function(ctx, module, callee, &call_args)?;
                            match outcome {
                                ExecOutcome::Returned(ret) => frame.set(*dst, ret.unwrap_or(Value::Null)),
                                ExecOutcome::Thrown(val) => return Ok(Some(val)),
                            }
                            return Ok(None);
                        }
                    }
                    if let Some(lazy_fn) = ctx.try_lookup_function(func_name) {
                        let outcome = super::exec_function(ctx, module, lazy_fn.as_ref(), &call_args)?;
                        match outcome {
                            ExecOutcome::Returned(ret) => frame.set(*dst, ret.unwrap_or(Value::Null)),
                            ExecOutcome::Thrown(val) => return Ok(Some(val)),
                        }
                        return Ok(None);
                    }
                }
                // Restore args for fallback paths below (call_args consumed obj_val).
                extra_args = call_args.into_iter().skip(1).collect();
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
            let callee_fn = module.func_index.get(func_name.as_str())
                .and_then(|&idx| module.functions.get(idx));
            let outcome = if let Some(callee) = callee_fn {
                super::exec_function(ctx, module, callee, &call_args)?
            } else if let Some(lazy_fn) = ctx.try_lookup_function(&func_name) {
                super::exec_function(ctx, module, lazy_fn.as_ref(), &call_args)?
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
            frame.set(*dst, ctx.static_get(field));
        }
        Instruction::StaticSet { field, val } => {
            let v = frame.get(*val)?.clone();
            ctx.static_set(field, v);
        }

        // ── Native interop ─────────────────────────────────────────────────
        //
        // C2 (`impl-tier1-c-abi`): `CallNative` flows through the registered
        // `RegisteredType` → libffi cif → marshal/unmarshal pipeline.
        // C4/C5 will wire the remaining three opcodes.
        Instruction::CallNative { dst, module, type_name, symbol, args } => {
            use crate::native::{marshal, dispatch as ndisp};

            let ty = ctx.resolve_native_type(module, type_name).ok_or_else(|| {
                anyhow::anyhow!(
                    "CallNative: unknown native type {module}::{type_name} (Z0905)"
                )
            })?;
            let method = ty.method(symbol).ok_or_else(|| {
                anyhow::anyhow!(
                    "CallNative: unknown method {module}::{type_name}::{symbol} (Z0905)"
                )
            })?;

            // Marshal each register into a Z42Value targeting the corresponding
            // ABI-side parameter type.
            if args.len() != method.params.len() {
                bail!(
                    "CallNative {module}::{type_name}::{symbol}: arity mismatch (caller passed {}, signature wants {})",
                    args.len(),
                    method.params.len()
                );
            }
            // Spec C8: marshal arena owns temporaries (e.g. CString backing
            // for `*const c_char`) for the call's duration; dropped after
            // dispatch returns.
            let mut arena = marshal::Arena::new();
            let z_args: Vec<z42_abi::Z42Value> = args
                .iter()
                .zip(method.params.iter())
                .map(|(reg, ty)| marshal::value_to_z42(frame.get(*reg)?, ty, &mut arena))
                .collect::<Result<_>>()?;

            // SAFETY: cif was built from `params`/`return_type` matching the
            // native function pointer at registration time; native lib keeps
            // the function alive via `VmContext.native_libs`. CURRENT_VM is
            // set by VmGuard so a re-entrant z42_* call finds the right ctx.
            let z_ret = unsafe {
                ndisp::call(
                    &method.cif,
                    method.fn_ptr,
                    &z_args,
                    &method.params,
                    &method.return_type,
                )
            }?;
            drop(arena);

            let result = marshal::z42_to_value(&z_ret, &method.return_type)?;
            frame.set(*dst, result);
        }
        Instruction::CallNativeVtable { vtable_slot, .. } => {
            bail!(
                "CallNativeVtable not yet implemented (Z0907, see spec C5 / impl-source-generator): slot={vtable_slot}"
            );
        }
        Instruction::PinPtr { dst, src } => {
            // C4: borrow a `String` / future `Array<u8>` buffer for FFI.
            // Caller (currently always test-emitted IR; user-side `pinned`
            // syntax lands in C5) is responsible for matching `UnpinPtr` on
            // every exit path. RC backend treats the borrow as zero-cost
            // (no relocation possible); the pin set will be repopulated
            // for moving GC backends in a later spec.
            let view = match frame.get(*src)? {
                Value::Str(s) => Value::PinnedView {
                    ptr: s.as_ptr() as u64,
                    len: s.len() as u64,
                    kind: crate::metadata::PinSourceKind::Str,
                },
                Value::Array(arr) => {
                    // Spec C10 — `Array<u8>` pin: snapshot the bytes into
                    // a Box<[u8]> owned by the VM for the pin's lifetime.
                    // Each element must be a `Value::I64` in 0..=255.
                    let arr_ref = arr.borrow();
                    let mut bytes = Vec::with_capacity(arr_ref.len());
                    for (i, v) in arr_ref.iter().enumerate() {
                        match v {
                            Value::I64(n) if (0..=255).contains(n) => {
                                bytes.push(*n as u8);
                            }
                            other => bail!(
                                "Z0908: PinPtr Array element {i} not a u8 in 0..=255: {other:?}"
                            ),
                        }
                    }
                    let len = bytes.len() as u64;
                    let buf: Box<[u8]> = bytes.into_boxed_slice();
                    let ptr = ctx.pin_owned_buffer(buf);
                    Value::PinnedView {
                        ptr,
                        len,
                        kind: crate::metadata::PinSourceKind::ArrayU8,
                    }
                }
                other => bail!(
                    "Z0908: PinPtr source must be String or Array<u8>, got {:?}",
                    other
                ),
            };
            frame.set(*dst, view);
        }
        Instruction::UnpinPtr { pinned } => {
            match frame.get(*pinned)? {
                Value::PinnedView { ptr, kind: crate::metadata::PinSourceKind::ArrayU8, .. } => {
                    // Spec C10: drop the snapshot Box<[u8]> we leaked into
                    // VmContext at PinPtr time.
                    ctx.release_owned_buffer(*ptr);
                }
                Value::PinnedView { .. } => {
                    // Str pin: borrowed from the source String — no-op.
                    // Future moving GC will deregister the entry from its
                    // pin set here.
                }
                other => bail!(
                    "Z0908: UnpinPtr expects PinnedView (compiler-emitted UnpinPtr should always pair with a prior PinPtr); got {:?}",
                    other
                ),
            }
        }
    }
    Ok(None)
}
