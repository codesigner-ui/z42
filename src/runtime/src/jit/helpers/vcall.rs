#![allow(dangerous_implicit_autorefs)]
//! Virtual dispatch (`jit_vcall`). Single-helper file because of size and
//! the L3-G4b primitive-as-struct + lazy-loader fallback paths it carries.
//! Mirrors `interp/exec_vcall.rs`.

use crate::metadata::Value;

use super::super::frame::{JitFrame, JitModuleCtx};
use super::{set_exception, vm_ctx_ref, JitFn};

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
