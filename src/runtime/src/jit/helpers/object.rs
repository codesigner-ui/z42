#![allow(dangerous_implicit_autorefs)]
//! Object allocation, field access, type tests, static fields, and the
//! generic `default(T)` runtime helper.

use crate::metadata::{NativeData, Value};
use std::collections::HashMap;

use super::super::frame::{JitFrame, JitModuleCtx};
use super::{set_exception, vm_ctx_ref, JitFn};

// ── Object allocation ────────────────────────────────────────────────────────

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
            id: crate::metadata::tokens::TypeId::UNRESOLVED,
        }));
    // 2026-05-02 fix-class-field-default-init: 按字段类型选默认值（与 interp
    // exec_object.rs::obj_new 镜像，共用 metadata::default_value_for）。
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
// dispatch — reads `frame.regs[0]` (this) → `ScriptObject.type_args[param_index]`
// → `default_value_for(tag)`. Non-Object reg 0 / OOB index / empty type_args
// → graceful Null. Note: JIT-allocated objects currently have empty type_args
// (jit_obj_new doesn't propagate them from the IR ObjNew yet), so this returns
// Null in JIT-only data-flow; interp path is the source of truth for full
// generic-T zero-value resolution.
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

// ── Field access ─────────────────────────────────────────────────────────────

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

// ── IsInstance / AsCast ──────────────────────────────────────────────────────
//
// Both helpers share the `is_subclass_or_eq` walk + the `is_array_isa`
// hardcoded array-base chain (2026-05-07 add-array-base-class).

pub(super) fn is_subclass_or_eq(module: &crate::metadata::Module, derived: &str, target: &str) -> bool {
    let mut cur = derived;
    loop {
        if cur == target { return true; }
        match module.classes.iter().find(|c| c.name == cur).and_then(|c| c.base_class.as_deref()) {
            Some(base) => cur = base,
            None       => return false,
        }
    }
}

// 2026-05-07 add-array-base-class: T[] is-a Std.Array is-a Std.Object.
// Mirror the interp `is_array_isa` hardcoded chain.
pub(super) fn is_array_isa(class_name: &str) -> bool {
    matches!(class_name, "Array" | "Object" | "Std.Array" | "Std.Object")
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
        Value::Array(_)   => is_array_isa(class_name),
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
        Value::Array(_)   => is_array_isa(class_name),
        Value::Null => true,
        _           => false,
    };
    (*frame).regs[dst as usize] = if is_match { val } else { Value::Null };
}

// ── Static fields ────────────────────────────────────────────────────────────

/// `jit_static_get` after formalize-jit-method-token Phase 2 (2026-05-08):
/// receives pre-resolved `StaticFieldId` directly. Resolver populates
/// `Function.resolved.static_field_tokens` at load via lazy ID allocation
/// (always succeeds), so JIT codegen embeds the id as i32 const.
#[no_mangle]
pub unsafe extern "C" fn jit_static_get(
    frame: *mut JitFrame, _ctx: *const JitModuleCtx,
    dst: u32, field_id: u32,
) {
    let id = crate::metadata::tokens::StaticFieldId(field_id);
    (*frame).regs[dst as usize] = vm_ctx_ref(_ctx).static_get_by_id(id);
}

#[no_mangle]
pub unsafe extern "C" fn jit_static_set(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    field_id: u32, val: u32,
) {
    let id = crate::metadata::tokens::StaticFieldId(field_id);
    let v = (*frame).regs[val as usize].clone();
    vm_ctx_ref(ctx).static_set_by_id(id, v);
}
