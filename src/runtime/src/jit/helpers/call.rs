#![allow(dangerous_implicit_autorefs)]
//! Direct call (`jit_call`) and corelib builtin dispatch (`jit_builtin`).

use crate::metadata::Value;

use super::super::frame::{FnEntry, JitFrame, JitModuleCtx};
use super::{set_exception, vm_ctx_ref, JitFn};

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
