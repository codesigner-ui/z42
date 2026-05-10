#![allow(dangerous_implicit_autorefs)]
//! Virtual dispatch (`jit_vcall`). Single-helper file because of size and
//! the L3-G4b primitive-as-struct + lazy-loader fallback paths it carries.
//! Mirrors `interp/exec_vcall.rs`.

use crate::metadata::Value;
use crate::metadata::resolver::VCallIC;

use super::super::frame::{JitFrame, JitModuleCtx};
use super::{set_exception, vm_ctx_ref, JitFn};

/// `jit_vcall` after formalize-jit-method-token Phase 2.E (2026-05-08):
/// the per-site `VCallIC` is threaded in (stable raw pointer baked into
/// machine code by codegen). Mirrors interp `vcall` — IC hit goes
/// straight to `fn_entries_by_id[cached_fn_idx]`; miss falls through
/// the existing primitive / vtable / lazy-loader paths and writes the
/// resolved (TypeId, vtable slot, MethodId) triple back to IC.
///
/// `ic_ptr` may be null when the resolver hasn't run (only happens in
/// tests bypassing `Vm::run`); helper degrades gracefully to slow path.
#[no_mangle]
pub unsafe extern "C" fn jit_vcall(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, obj: u32, method_ptr: *const u8, method_len: usize,
    args_ptr: *const u32, argc: usize,
    ic_ptr: *const VCallIC,
    caller_line: u32,   // 2026-05-10 jit-stack-trace
    caller_col:  u32,   // 2026-05-10 span-column-propagate
) -> u8 {
    use std::sync::atomic::Ordering;

    let method    = std::str::from_utf8(std::slice::from_raw_parts(method_ptr, method_len))
        .unwrap_or("<invalid>");
    let ctx_ref   = &*ctx;
    let module    = &*ctx_ref.module;
    let frame_ref = &mut *frame;

    // jit-stack-trace: stamp caller's call-site line once at entry; each
    // invoke path below pushes the callee frame info before running.
    vm_ctx_ref(ctx).update_top_frame_pos(caller_line, caller_col);

    let obj_val = frame_ref.regs[obj as usize].clone();
    let arg_regs = std::slice::from_raw_parts(args_ptr, argc);
    let mut extra_args: Vec<Value> = arg_regs.iter().map(|&r| frame_ref.regs[r as usize].clone()).collect();

    // ── IC fast path ────────────────────────────────────────────────────
    // Only applies when (1) IC pointer non-null, (2) receiver is an object
    // (primitives go through the dedicated primitive_class_name path
    // below), (3) receiver TypeId matches cache, (4) cached_fn_idx
    // resolves to an entry in `fn_entries_by_id`.
    if !ic_ptr.is_null() {
        if let Value::Object(ref rc) = obj_val {
            let recv_type = rc.borrow().type_desc.id.0;
            let cached_type = (*ic_ptr).cached_type_id.load(Ordering::Relaxed);
            if recv_type != crate::metadata::tokens::UNRESOLVED && recv_type == cached_type {
                let fn_idx = (*ic_ptr).cached_fn_idx.load(Ordering::Relaxed);
                if fn_idx != crate::metadata::tokens::UNRESOLVED {
                    if let Some(entry) = ctx_ref.fn_entries_by_id.get(fn_idx as usize).cloned().flatten() {
                        let mut call_args: Vec<Value> = vec![obj_val.clone()];
                        call_args.append(&mut extra_args);
                        let mut callee = JitFrame::new(entry.max_reg, &call_args);
                        let jit_fn: JitFn = std::mem::transmute(entry.ptr);
                        let vm_ctx = vm_ctx_ref(ctx);
                        vm_ctx.push_call_frame(crate::exception::FrameInfo::new(
                            entry.name.to_string(), entry.file.to_string()));
                        vm_ctx.push_frame_state(&callee.regs as *const _, &callee.env_arena as *const _);
                        let r = jit_fn(&mut callee, ctx);
                        vm_ctx.pop_frame_regs();
                        vm_ctx.pop_call_frame();
                        if r != 0 { callee.recycle(); return 1; }
                        frame_ref.regs[dst as usize] = callee.ret.take().unwrap_or(Value::Null);
                        callee.recycle();
                        return 0;
                    }
                }
            }
        }
    }

    // L3-G4b primitive-as-struct: primitives dispatch through their stdlib struct's
    // method — construct `{Std.int | Std.double | ...}.{method}` and invoke via the
    // JIT entry cache. Replaces the old hardcoded `(Value, method) → builtin` table.
    //
    // Overload resolution: when the receiver type is statically `object` the IR
    // carries the unmangled method name (e.g. `Equals`), but IrGen emits overloaded
    // methods with a `$<arity>` suffix (e.g. `Std.String.Equals$1`). When the
    // unmangled lookup misses we retry with the arity-suffixed name. Mirrors
    // `interp/exec_vcall.rs::vcall`. Subsumes the legacy `Value::Str`
    // hardcoded `__str_*` fallback (review2 §2.2).
    if let Some(class_name) = crate::interp::primitive_class_name(&obj_val) {
        let mut call_args = vec![obj_val.clone()];
        call_args.append(&mut extra_args);
        let arity = call_args.len() - 1; // exclude `this`
        let primary = format!("{}.{}", class_name, method);
        let overload = format!("{}.{}${}", class_name, method, arity);
        for func_name in [primary.as_str(), overload.as_str()] {
            if let Some(entry) = ctx_ref.fn_entries.get(func_name) {
                let entry = entry.clone();
                let mut callee = JitFrame::new(entry.max_reg, &call_args);
                let jit_fn: JitFn = std::mem::transmute(entry.ptr);
                // Phase 3f-2: GC roots scan + jit-stack-trace call_frame push.
                let vm_ctx = vm_ctx_ref(ctx);
                vm_ctx.push_call_frame(crate::exception::FrameInfo::new(
                    entry.name.to_string(), entry.file.to_string()));
                vm_ctx.push_frame_state(&callee.regs as *const _, &callee.env_arena as *const _);
                let r = jit_fn(&mut callee, ctx);
                vm_ctx.pop_frame_regs();
                vm_ctx.pop_call_frame();
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

    let (class_name, recv_type_id) = match &obj_val {
        Value::Object(rc) => {
            let b = rc.borrow();
            (b.type_desc.name.clone(), b.type_desc.id.0)
        }
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("VCall: expected object, got {:?}", other)));
            return 1;
        }
    };

    let func_name = match resolve_virtual(module, &class_name, method) {
        Ok(n)  => n,
        Err(e) => { set_exception(vm_ctx_ref(ctx), Value::Str(e.to_string())); return 1; }
    };

    // IC update: cache (recv_type_id, fn_idx) for next time this site sees
    // the same receiver type. Slot index isn't tracked here (resolve_virtual
    // walks by name, not vtable index) — store UNRESOLVED so the slot is
    // visible-but-unused; the IC fast path only consults `cached_fn_idx`.
    if !ic_ptr.is_null() && recv_type_id != crate::metadata::tokens::UNRESOLVED {
        if let Some(&fn_idx) = module.func_index.get(&func_name) {
            (*ic_ptr).cached_type_id.store(recv_type_id, Ordering::Relaxed);
            (*ic_ptr).cached_fn_idx.store(fn_idx as u32, Ordering::Relaxed);
        }
    }

    let entry = match ctx_ref.fn_entries.get(&func_name) {
        Some(e) => e.clone(),
        None => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("VCall: compiled entry for `{}` not found", func_name)));
            return 1;
        }
    };

    let mut call_args: Vec<Value> = vec![obj_val];
    call_args.append(&mut extra_args);
    let mut callee = JitFrame::new(entry.max_reg, &call_args);
    let jit_fn: JitFn = std::mem::transmute(entry.ptr);
    // Phase 3f-2: GC roots scan + jit-stack-trace call_frame push.
    let vm_ctx = vm_ctx_ref(ctx);
    vm_ctx.push_call_frame(crate::exception::FrameInfo::new(
        entry.name.to_string(), entry.file.to_string()));
    vm_ctx.push_frame_state(&callee.regs as *const _, &callee.env_arena as *const _);
    let r = jit_fn(&mut callee, ctx);
    vm_ctx.pop_frame_regs();
    vm_ctx.pop_call_frame();
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
