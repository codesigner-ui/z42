#![allow(dangerous_implicit_autorefs)]
//! Direct call (`jit_call`) and corelib builtin dispatch (`jit_builtin`).

use crate::metadata::Value;

use super::super::frame::{FnEntry, JitFrame, JitModuleCtx};
use super::{set_exception, vm_ctx_ref, JitFn};

/// `jit_call` after formalize-jit-method-token Phase 2.C (2026-05-08):
/// hot path takes pre-resolved `MethodId` and indexes `fn_entries_by_id`
/// directly. On `UNRESOLVED` (cross-zpkg), falls back to name-based
/// HashMap lookup. Name pointer kept for diagnostics + fallback.
#[no_mangle]
pub unsafe extern "C" fn jit_call(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32,
    method_id: u32,
    fn_name_ptr: *const u8, fn_name_len: usize,
    args_ptr: *const u32, argc: usize,
) -> u8 {
    let ctx_ref   = &*ctx;
    let frame_ref = &mut *frame;

    // Hot path: direct Vec[id] index when token resolved.
    let entry_opt: Option<FnEntry> =
        if method_id != crate::metadata::tokens::UNRESOLVED {
            ctx_ref.fn_entries_by_id.get(method_id as usize).copied().flatten()
        } else {
            None
        };

    let entry: FnEntry = match entry_opt {
        Some(e) => e,
        None => {
            // Cross-zpkg / fallback: by-name HashMap lookup.
            let func_name = std::str::from_utf8(std::slice::from_raw_parts(fn_name_ptr, fn_name_len))
                .unwrap_or("<invalid>");
            match ctx_ref.fn_entries.get(func_name) {
                Some(&e) => e,
                None => {
                    set_exception(vm_ctx_ref(ctx), Value::Str(format!("undefined function `{}`", func_name)));
                    return 1;
                }
            }
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

/// `jit_builtin` after `formalize-jit-method-token` (2026-05-08): receives
/// pre-resolved `BuiltinId` directly (not name pointers). Resolver
/// guarantees every `Instruction::Builtin.name` resolves at module load
/// (closed set; panic on miss), so JIT codegen embeds the id as an i32
/// constant in the generated machine code. Helper indexes
/// `BUILTINS[id]` directly — zero hash on every call.
#[no_mangle]
pub unsafe extern "C" fn jit_builtin(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32,
    builtin_id: u32,
    args_ptr: *const u32, argc: usize,
) -> u8 {
    let frame_ref = &mut *frame;
    let arg_regs  = std::slice::from_raw_parts(args_ptr, argc);
    let args: Vec<Value> = arg_regs.iter().map(|&r| frame_ref.regs[r as usize].clone()).collect();

    let id = crate::metadata::tokens::BuiltinId(builtin_id);
    match crate::corelib::exec_builtin_by_id(vm_ctx_ref(ctx), id, &args) {
        Ok(v)  => { frame_ref.regs[dst as usize] = v; 0 }
        Err(e) => { set_exception(vm_ctx_ref(ctx), Value::Str(e.to_string())); 1 }
    }
}
