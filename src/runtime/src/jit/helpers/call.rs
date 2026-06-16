#![allow(dangerous_implicit_autorefs)]
//! Direct call (`jit_call`) and corelib builtin dispatch (`jit_builtin`).

use crate::metadata::Value;

use super::super::frame::{FnEntry, JitFrame, JitModuleCtx};
use super::{set_exception, vm_ctx_ref, JitFn};

/// `jit_call` after formalize-jit-method-token Phase 2.C (2026-05-08):
/// hot path takes pre-resolved `MethodId` and indexes `fn_entries_by_id`
/// directly. On `UNRESOLVED` (cross-zpkg), falls back to name-based
/// HashMap lookup. Name pointer kept for diagnostics + fallback.
///
/// `caller_line` / `caller_col` (jit-stack-trace + span-column-propagate,
/// 2026-05-10) are the source position of this call site — codegen passes
/// both as constants. Stamped onto the caller's FrameInfo before descending
/// so a downstream throw's snapshot shows the precise call site.
/// `caller_col == 0` means unknown (zbc < 1.1) — formatter degrades to
/// `(file:line)`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn jit_call(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32,
    method_id: u32,
    fn_name_ptr: *const u8, fn_name_len: usize,
    args_ptr: *const u32, argc: usize,
    caller_line: u32,
    caller_col:  u32,
) -> u8 {
    let ctx_ref   = &*ctx;
    let frame_ref = &mut *frame;

    // Hot path: direct Vec[id] index when token resolved.
    let entry_opt: Option<FnEntry> =
        if method_id != crate::metadata::tokens::UNRESOLVED {
            ctx_ref.fn_entries_by_id.get(method_id as usize).cloned().flatten()
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
                Some(e) => e.clone(),
                None => {
                    set_exception(vm_ctx_ref(ctx), Value::Str(format!("undefined function `{}`", func_name).into()));
                    return 1;
                }
            }
        }
    };

    let arg_regs = std::slice::from_raw_parts(args_ptr, argc);
    let args: Vec<Value> = arg_regs.iter().map(|&r| frame_ref.regs[r as usize].clone()).collect();

    let mut callee_frame = JitFrame::new(entry.max_reg, &args);
    let jit_fn: JitFn = std::mem::transmute(entry.ptr);
    let vm_ctx = vm_ctx_ref(ctx);

    // jit-stack-trace + span-column-propagate: stamp caller's site pos.
    vm_ctx.update_top_frame_pos(caller_line, caller_col);
    // 2026-05-10 unify-frame-chain: one push covering GC roots + trace.
    vm_ctx.push_frame(crate::exception::VmFrame::new(
        entry.name.to_string(),
        entry.file.to_string(),
        &callee_frame.regs as *const _,
        &callee_frame.env_arena as *const _,
    ));
    let result = jit_fn(&mut callee_frame, ctx);
    vm_ctx.pop_frame();
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
#[unsafe(no_mangle)]
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
    let vm = vm_ctx_ref(ctx);
    match crate::corelib::exec_builtin_by_id(vm, id, &args) {
        Ok(v)  => { frame_ref.regs[dst as usize] = v; 0 }
        Err(e) => {
            // make-corelib-errors-catchable parity (this path was interp-only;
            // jit_builtin previously set a raw `Value::Str`). Wrap the builtin
            // error in a `Std.Exception` so JIT-compiled code can catch it with
            // `catch (Exception e)` — a raw string never matches the catch type.
            // Falls back to the raw string if `Std.Exception` isn't loaded.
            let module = &*(*ctx).module;
            let exc = match crate::exception::make_stdlib_exception(
                vm, module, "Std.Exception", e.to_string(),
            ) {
                Ok(exc) => exc,
                Err(_)  => Value::Str(e.to_string().into()),
            };
            set_exception(vm, exc);
            1
        }
    }
}
