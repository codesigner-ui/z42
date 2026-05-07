#![allow(dangerous_implicit_autorefs)]
// JIT helpers — function calls, arrays, objects, type checks, static fields.

use crate::metadata::{NativeData, Value};
use std::collections::HashMap;

use super::frame::{FnEntry, JitFrame, JitModuleCtx};
use super::helpers::{set_exception, vm_ctx_ref, JitFn};

// ── Calls ────────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_call(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, fn_name_ptr: *const u8, fn_name_len: usize,
    args_ptr: *const u32, argc: usize,
) -> u8 {
    let func_name = std::str::from_utf8(std::slice::from_raw_parts(fn_name_ptr, fn_name_len))
        .unwrap_or("<invalid>");
    let ctx_ref   = &*ctx;
    let frame_ref = &mut *frame;

    let entry: &FnEntry = match ctx_ref.fn_entries.get(func_name) {
        Some(e) => e,
        None => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("undefined function `{}`", func_name)));
            return 1;
        }
    };

    let arg_regs = std::slice::from_raw_parts(args_ptr, argc);
    let args: Vec<Value> = arg_regs.iter().map(|&r| frame_ref.regs[r as usize].clone()).collect();

    let mut callee_frame = JitFrame::new(entry.max_reg, &args);
    let jit_fn: JitFn = std::mem::transmute(entry.ptr);
    // Phase 3f-2: push callee frame regs to GC roots scan during this jit_fn call.
    let vm_ctx = vm_ctx_ref(ctx);
    vm_ctx.push_frame_state(&callee_frame.regs as *const _, &callee_frame.env_arena as *const _);
    let result = jit_fn(&mut callee_frame, ctx);
    vm_ctx.pop_frame_regs();
    if result != 0 { callee_frame.recycle(); return 1; }
    frame_ref.regs[dst as usize] = callee_frame.ret.take().unwrap_or(Value::Null);
    callee_frame.recycle();
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_builtin(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32,
    name_ptr: *const u8, name_len: usize,
    args_ptr: *const u32, argc: usize,
) -> u8 {
    let name = std::str::from_utf8(std::slice::from_raw_parts(name_ptr, name_len))
        .unwrap_or("<invalid>");
    let frame_ref = &mut *frame;
    let arg_regs  = std::slice::from_raw_parts(args_ptr, argc);
    let args: Vec<Value> = arg_regs.iter().map(|&r| frame_ref.regs[r as usize].clone()).collect();

    match crate::corelib::exec_builtin(vm_ctx_ref(ctx), name, &args) {
        Ok(v)  => { frame_ref.regs[dst as usize] = v; 0 }
        Err(e) => { set_exception(vm_ctx_ref(ctx), Value::Str(e.to_string())); 1 }
    }
}

// ── Arrays ───────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_array_new(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, size: u32,
) -> u8 {
    let n = match &(*frame).regs[size as usize] {
        Value::I64(n) if *n >= 0 => *n as usize,
        Value::I64(n) if *n >= 0 => *n as usize,
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArrayNew: expected non-negative int, got {:?}", other)));
            return 1;
        }
    };
    (*frame).regs[dst as usize] = vm_ctx_ref(ctx).heap().alloc_array(vec![Value::Null; n]);
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_new_lit(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, elems_ptr: *const u32, elem_cnt: usize,
) {
    let elems = std::slice::from_raw_parts(elems_ptr, elem_cnt);
    let vals: Vec<Value> = elems.iter().map(|&r| (*frame).regs[r as usize].clone()).collect();
    (*frame).regs[dst as usize] = vm_ctx_ref(ctx).heap().alloc_array(vals);
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_get(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, arr: u32, idx: u32,
) -> u8 {
    let arr_val = (*frame).regs[arr as usize].clone();
    let idx_val = (*frame).regs[idx as usize].clone();
    let result = match &arr_val {
        Value::Array(rc) => {
            let i = match &idx_val {
                Value::I64(n) if *n >= 0 => *n as usize,
                Value::I64(n) if *n >= 0 => *n as usize,
                other => {
                    set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArrayGet: bad index {:?}", other)));
                    return 1;
                }
            };
            let borrowed = rc.borrow();
            if i >= borrowed.len() {
                set_exception(vm_ctx_ref(ctx), Value::Str(format!("array index {} out of bounds (len={})", i, borrowed.len())));
                return 1;
            }
            borrowed[i].clone()
        }
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArrayGet: expected array, got {:?}", other)));
            return 1;
        }
    };
    (*frame).regs[dst as usize] = result;
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_set(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    arr: u32, idx: u32, val: u32,
) -> u8 {
    let arr_val = (*frame).regs[arr as usize].clone();
    let idx_val = (*frame).regs[idx as usize].clone();
    let v       = (*frame).regs[val as usize].clone();
    match &arr_val {
        Value::Array(rc) => {
            let i = match &idx_val {
                Value::I64(n) if *n >= 0 => *n as usize,
                Value::I64(n) if *n >= 0 => *n as usize,
                other => {
                    set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArraySet: bad index {:?}", other)));
                    return 1;
                }
            };
            let mut borrowed = rc.borrow_mut();
            if i >= borrowed.len() {
                set_exception(vm_ctx_ref(ctx), Value::Str(format!("array index {} out of bounds (len={})", i, borrowed.len())));
                return 1;
            }
            borrowed[i] = v;
        }
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArraySet: expected array, got {:?}", other)));
            return 1;
        }
    }
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_len(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, arr: u32,
) -> u8 {
    match &(*frame).regs[arr as usize] {
        Value::Array(rc) => { (*frame).regs[dst as usize] = Value::I64(rc.borrow().len() as i64); 0 }
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArrayLen: expected array, got {:?}", other)));
            1
        }
    }
}

// ── Objects ──────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_obj_new(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32,
    cls_name_ptr: *const u8, cls_name_len: usize,
    ctor_name_ptr: *const u8, ctor_name_len: usize,
    args_ptr: *const u32, argc: usize,
    // 2026-05-07 expand-jit-type-args: per-instance generic type-args (D-8b-3
    // Phase 2 JIT path). `type_args_ptr` is a `*const String` directly into the
    // IR `Instruction::ObjNew { type_args: Vec<String> }` storage, valid for
    // module lifetime. Non-generic ObjNew passes count = 0.
    type_args_ptr: *const String, type_args_count: usize,
) -> u8 {
    let class_name = std::str::from_utf8(std::slice::from_raw_parts(cls_name_ptr, cls_name_len))
        .unwrap_or("<invalid>").to_string();
    let ctor_name = std::str::from_utf8(std::slice::from_raw_parts(ctor_name_ptr, ctor_name_len))
        .unwrap_or("<invalid>").to_string();
    let ctx_ref   = &*ctx;
    let module    = &*ctx_ref.module;
    let frame_ref = &mut *frame;

    let type_desc = module.type_registry.get(&class_name).cloned()
        .unwrap_or_else(|| std::sync::Arc::new(crate::metadata::TypeDesc {
            name: class_name.clone(), base_name: None,
            fields: Vec::new(), field_index: HashMap::new(),
            vtable: Vec::new(), vtable_index: HashMap::new(), type_params: vec![], type_args: vec![],
            type_param_constraints: vec![],
        }));
    // 2026-05-02 fix-class-field-default-init: 按字段类型选默认值（与 interp
    // exec_instr.rs::ObjNew 镜像，共用 metadata::default_value_for）。
    let slots: Vec<Value> = type_desc.fields.iter()
        .map(|f| crate::metadata::default_value_for(&f.type_tag))
        .collect();
    let obj_val = vm_ctx_ref(ctx).heap().alloc_object(type_desc, slots, NativeData::None);

    // 2026-05-07 expand-jit-type-args: populate per-instance type_args BEFORE
    // ctor call so the ctor body's `default(T)` resolves correctly (mirrors
    // interp ObjNew handler order).
    if type_args_count > 0 {
        if let Value::Object(ref rc) = obj_val {
            let slice = std::slice::from_raw_parts(type_args_ptr, type_args_count);
            rc.borrow_mut().type_args = slice.iter().cloned().collect();
        }
    }

    let arg_regs = std::slice::from_raw_parts(args_ptr, argc);
    let mut ctor_args: Vec<Value> = vec![obj_val.clone()];
    ctor_args.extend(arg_regs.iter().map(|&r| frame_ref.regs[r as usize].clone()));

    // 直查 ctor_name (TypeChecker 已 overload-resolve)；无名字推断。
    if let Some(entry) = ctx_ref.fn_entries.get(&ctor_name) {
        let mut callee = JitFrame::new(entry.max_reg, &ctor_args);
        let jit_fn: JitFn = std::mem::transmute(entry.ptr);
        // Phase 3f-2: 注册 callee frame regs 给 GC root scanner
        let vm_ctx = vm_ctx_ref(ctx);
        vm_ctx.push_frame_state(&callee.regs as *const _, &callee.env_arena as *const _);
        let r = jit_fn(&mut callee, ctx);
        vm_ctx.pop_frame_regs();
        callee.recycle();
        if r != 0 { return 1; }
    }
    frame_ref.regs[dst as usize] = obj_val;
    0
}

// 2026-05-07 add-default-generic-typeparam (D-8b-3 Phase 2): JIT helper for
// `default(T)` runtime resolution. Mirrors interp `Instruction::DefaultOf`
// dispatch in src/interp/exec_instr.rs — reads `frame.regs[0]` (this) →
// `ScriptObject.type_args[param_index]` → `default_value_for(tag)`. Non-Object
// reg 0 / OOB index / empty type_args → graceful Null. Note: JIT-allocated
// objects currently have empty type_args (jit_obj_new doesn't propagate them
// from the IR ObjNew yet), so this returns Null in JIT-only data-flow; interp
// path is the source of truth for full generic-T zero-value resolution.
#[no_mangle]
pub unsafe extern "C" fn jit_default_of(
    _frame: *mut JitFrame, _ctx: *const JitModuleCtx,
    dst: u32, param_index: u32,
) -> u8 {
    let frame_ref = &mut *_frame;
    let val = match frame_ref.regs.first() {
        Some(Value::Object(rc)) => {
            let b = rc.borrow();
            b.type_args.get(param_index as usize)
                .map(|tag| crate::metadata::types::default_value_for(tag))
                .unwrap_or(Value::Null)
        }
        _ => Value::Null,
    };
    frame_ref.regs[dst as usize] = val;
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_field_get(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, obj: u32,
    field_name_ptr: *const u8, field_name_len: usize,
) -> u8 {
    let field_name = std::str::from_utf8(std::slice::from_raw_parts(field_name_ptr, field_name_len))
        .unwrap_or("<invalid>");
    let obj_val = &(*frame).regs[obj as usize];
    let val = match obj_val {
        Value::Object(rc) => {
            let b = rc.borrow();
            if let Some(&slot) = b.type_desc.field_index.get(field_name) {
                b.slots.get(slot).cloned().unwrap_or(Value::Null)
            } else { Value::Null }
        }
        Value::Str(s) if field_name == "Length" => Value::I64(s.chars().count() as i64),
        Value::Array(rc) if field_name == "Length" || field_name == "Count" => Value::I64(rc.borrow().len() as i64),
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("FieldGet: expected object, got {:?}", other)));
            return 1;
        }
    };
    (*frame).regs[dst as usize] = val;
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_field_set(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    obj: u32,
    field_name_ptr: *const u8, field_name_len: usize, val: u32,
) -> u8 {
    let field_name = std::str::from_utf8(std::slice::from_raw_parts(field_name_ptr, field_name_len))
        .unwrap_or("<invalid>").to_string();
    let v = (*frame).regs[val as usize].clone();
    match &(*frame).regs[obj as usize] {
        Value::Object(rc) => {
            let mut b = rc.borrow_mut();
            if let Some(&slot) = b.type_desc.field_index.get(&field_name) {
                if slot < b.slots.len() { b.slots[slot] = v; }
            }
            0
        }
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("FieldSet: expected object, got {:?}", other)));
            1
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_vcall(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, obj: u32, method_ptr: *const u8, method_len: usize,
    args_ptr: *const u32, argc: usize,
) -> u8 {
    let method    = std::str::from_utf8(std::slice::from_raw_parts(method_ptr, method_len))
        .unwrap_or("<invalid>");
    let ctx_ref   = &*ctx;
    let module    = &*ctx_ref.module;
    let frame_ref = &mut *frame;

    let obj_val = frame_ref.regs[obj as usize].clone();
    let arg_regs = std::slice::from_raw_parts(args_ptr, argc);
    let mut extra_args: Vec<Value> = arg_regs.iter().map(|&r| frame_ref.regs[r as usize].clone()).collect();

    // L3-G4b primitive-as-struct: primitives dispatch through their stdlib struct's
    // method — construct `{Std.int | Std.double | ...}.{method}` and invoke via the
    // JIT entry cache. Replaces the old hardcoded `(Value, method) → builtin` table.
    //
    // Overload resolution: when the receiver type is statically `object` the IR
    // carries the unmangled method name (e.g. `Equals`), but IrGen emits overloaded
    // methods with a `$<arity>` suffix (e.g. `Std.String.Equals$1`). When the
    // unmangled lookup misses we retry with the arity-suffixed name. Mirrors
    // `interp/exec_instr.rs::Instruction::VCall`. Subsumes the legacy `Value::Str`
    // hardcoded `__str_*` fallback (review2 §2.2).
    if let Some(class_name) = crate::interp::exec_instr::primitive_class_name(&obj_val) {
        let mut call_args = vec![obj_val.clone()];
        call_args.append(&mut extra_args);
        let arity = call_args.len() - 1; // exclude `this`
        let primary = format!("{}.{}", class_name, method);
        let overload = format!("{}.{}${}", class_name, method, arity);
        for func_name in [primary.as_str(), overload.as_str()] {
            if let Some(entry) = ctx_ref.fn_entries.get(func_name) {
                let mut callee = JitFrame::new(entry.max_reg, &call_args);
                let jit_fn: JitFn = std::mem::transmute(entry.ptr);
                // Phase 3f-2: GC roots scan
                let vm_ctx = vm_ctx_ref(ctx);
                vm_ctx.push_frame_state(&callee.regs as *const _, &callee.env_arena as *const _);
                let r = jit_fn(&mut callee, ctx);
                vm_ctx.pop_frame_regs();
                if r != 0 { callee.recycle(); return 1; }
                frame_ref.regs[dst as usize] = callee.ret.take().unwrap_or(Value::Null);
                callee.recycle();
                return 0;
            }
            // Lazy loader fallback — call via interpreter.
            // Reach VmContext through the JIT module ctx pointer (set by
            // JitModule::run for the duration of this entry call).
            let vm_ctx = vm_ctx_ref(ctx);
            if let Some(lazy_fn) = vm_ctx.try_lookup_function(func_name) {
                match crate::interp::exec_function(vm_ctx, module, lazy_fn.as_ref(), &call_args) {
                    Ok(outcome) => match outcome {
                        crate::interp::ExecOutcome::Returned(ret) => {
                            frame_ref.regs[dst as usize] = ret.unwrap_or(Value::Null);
                            return 0;
                        }
                        crate::interp::ExecOutcome::Thrown(val) => {
                            set_exception(vm_ctx, val);
                            return 1;
                        }
                    },
                    Err(e) => { set_exception(vm_ctx, Value::Str(e.to_string())); return 1; }
                }
            }
        }
        // Restore args for fallback paths.
        extra_args = call_args.into_iter().skip(1).collect();
    }

    let class_name = match &obj_val {
        Value::Object(rc) => rc.borrow().type_desc.name.clone(),
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("VCall: expected object, got {:?}", other)));
            return 1;
        }
    };

    let func_name = match resolve_virtual(module, &class_name, method) {
        Ok(n)  => n,
        Err(e) => { set_exception(vm_ctx_ref(ctx), Value::Str(e.to_string())); return 1; }
    };

    let entry = match ctx_ref.fn_entries.get(&func_name) {
        Some(e) => e,
        None => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("VCall: compiled entry for `{}` not found", func_name)));
            return 1;
        }
    };

    let mut call_args: Vec<Value> = vec![obj_val];
    call_args.append(&mut extra_args);
    let mut callee = JitFrame::new(entry.max_reg, &call_args);
    let jit_fn: JitFn = std::mem::transmute(entry.ptr);
    // Phase 3f-2: GC roots scan
    let vm_ctx = vm_ctx_ref(ctx);
    vm_ctx.push_frame_state(&callee.regs as *const _, &callee.env_arena as *const _);
    let r = jit_fn(&mut callee, ctx);
    vm_ctx.pop_frame_regs();
    if r != 0 { callee.recycle(); return 1; }
    frame_ref.regs[dst as usize] = callee.ret.take().unwrap_or(Value::Null);
    callee.recycle();
    0
}

fn resolve_virtual(module: &crate::metadata::Module, class_name: &str, method: &str) -> anyhow::Result<String> {
    let mut cur = class_name;
    loop {
        let qualified = format!("{}.{}", cur, method);
        if module.functions.iter().any(|f| f.name == qualified) { return Ok(qualified); }
        match module.classes.iter().find(|c| c.name == cur).and_then(|c| c.base_class.as_deref()) {
            Some(base) => cur = base,
            None => anyhow::bail!("VCall: no implementation of `{}` found in hierarchy of `{}`", method, class_name),
        }
    }
}

// ── IsInstance / AsCast ──────────────────────────────────────────────────────

fn is_subclass_or_eq(module: &crate::metadata::Module, derived: &str, target: &str) -> bool {
    let mut cur = derived;
    loop {
        if cur == target { return true; }
        match module.classes.iter().find(|c| c.name == cur).and_then(|c| c.base_class.as_deref()) {
            Some(base) => cur = base,
            None       => return false,
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_is_instance(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, obj: u32, cls_ptr: *const u8, cls_len: usize,
) {
    let class_name = std::str::from_utf8(std::slice::from_raw_parts(cls_ptr, cls_len))
        .unwrap_or("<invalid>");
    let module = &*(*ctx).module;
    let result = match &(*frame).regs[obj as usize] {
        Value::Object(rc) => is_subclass_or_eq(module, &rc.borrow().type_desc.name, class_name),
        _ => false,
    };
    (*frame).regs[dst as usize] = Value::Bool(result);
}

#[no_mangle]
pub unsafe extern "C" fn jit_as_cast(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, obj: u32, cls_ptr: *const u8, cls_len: usize,
) {
    let class_name = std::str::from_utf8(std::slice::from_raw_parts(cls_ptr, cls_len))
        .unwrap_or("<invalid>");
    let module = &*(*ctx).module;
    let val    = (*frame).regs[obj as usize].clone();
    let is_match = match &val {
        Value::Object(rc) => is_subclass_or_eq(module, &rc.borrow().type_desc.name, class_name),
        Value::Null => true,
        _           => false,
    };
    (*frame).regs[dst as usize] = if is_match { val } else { Value::Null };
}

// ── Static fields ────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_static_get(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, field_ptr: *const u8, field_len: usize,
) {
    let field = std::str::from_utf8(std::slice::from_raw_parts(field_ptr, field_len))
        .unwrap_or("<invalid>");
    (*frame).regs[dst as usize] = vm_ctx_ref(ctx).static_get(field);
}

#[no_mangle]
pub unsafe extern "C" fn jit_static_set(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    field_ptr: *const u8, field_len: usize, val: u32,
) {
    let field = std::str::from_utf8(std::slice::from_raw_parts(field_ptr, field_len))
        .unwrap_or("<invalid>").to_string();
    let v = (*frame).regs[val as usize].clone();
    vm_ctx_ref(ctx).static_set(&field, v);
}
