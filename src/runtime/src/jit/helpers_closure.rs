#![allow(dangerous_implicit_autorefs)]
//! L3 closure JIT helpers — `LoadFn` / `MkClos` / `CallIndirect`.
//!
//! Behaviour mirrors `interp::exec_instr` (impl-closure-l3-core); see
//! `docs/design/closure.md` §6 + `spec/archive/2026-05-02-impl-closure-l3-jit-complete/`.
//!
//! Convention follows the rest of `jit/helpers_*`:
//!   • Every helper takes `frame: *mut JitFrame, ctx: *const JitModuleCtx` first.
//!   • Returns `u8`: 0 on success, 1 on exception (set via `set_exception`).
//!   • Strings / register-index slices are passed as `(ptr, len)` pairs whose
//!     storage lives inside the `Module` bytecode (lifetime ≥ JitModule).

use crate::metadata::Value;

use super::frame::{FnEntry, JitFrame, JitModuleCtx};
use super::helpers::{set_exception, vm_ctx_ref, JitFn};

// ── LoadFn ────────────────────────────────────────────────────────────────────

/// Push `Value::FuncRef(name)` into `frame.regs[dst]`. No-capture lambdas /
/// local fns lower to this. See closure.md §6 + L3-C-2.
#[no_mangle]
pub unsafe extern "C" fn jit_load_fn(
    frame: *mut JitFrame, _ctx: *const JitModuleCtx,
    dst: u32,
    name_ptr: *const u8, name_len: usize,
) -> u8 {
    let name = std::str::from_utf8(std::slice::from_raw_parts(name_ptr, name_len))
        .unwrap_or("<invalid>");
    (*frame).regs[dst as usize] = Value::FuncRef(name.to_string());
    0
}

// ── MkClos ────────────────────────────────────────────────────────────────────

/// Allocate a heap env from `captures` registers and write
/// `Value::Closure { env, fn_name }` to `frame.regs[dst]`.
/// See closure.md §6 + L3-C-5.
#[no_mangle]
pub unsafe extern "C" fn jit_mk_clos(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32,
    name_ptr: *const u8, name_len: usize,
    caps_ptr: *const u32, caps_len: usize,
) -> u8 {
    let name = std::str::from_utf8(std::slice::from_raw_parts(name_ptr, name_len))
        .unwrap_or("<invalid>")
        .to_string();
    let frame_ref = &mut *frame;
    let cap_regs  = std::slice::from_raw_parts(caps_ptr, caps_len);
    let env_vec: Vec<Value> = cap_regs.iter()
        .map(|&r| frame_ref.regs[r as usize].clone())
        .collect();

    // Allocate env via the GC heap so it's tracked as a managed array.
    let env_val = vm_ctx_ref(ctx).heap().alloc_array(env_vec);
    let env = match env_val {
        Value::Array(rc) => rc,
        _ => unreachable!("alloc_array must return Value::Array"),
    };
    frame_ref.regs[dst as usize] = Value::Closure { env, fn_name: name };
    0
}

// ── CallIndirect ──────────────────────────────────────────────────────────────

/// Invoke whatever callable lives in `frame.regs[callee]`:
///   • `Value::FuncRef(name)` → static call (parameters as-is)
///   • `Value::Closure { env, fn_name }` → prepend env as implicit first arg
/// Anything else → exception. See closure.md §6 + L3-C-6.
#[no_mangle]
pub unsafe extern "C" fn jit_call_indirect(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, callee: u32,
    args_ptr: *const u32, args_len: usize,
) -> u8 {
    let frame_ref = &mut *frame;
    let ctx_ref   = &*ctx;
    let vm_ctx    = vm_ctx_ref(ctx);

    // 1) Resolve callee Value into (fn_name, optional env to prepend).
    let (fn_name, env_opt): (String, Option<_>) = match &frame_ref.regs[callee as usize] {
        Value::FuncRef(n) => (n.clone(), None),
        Value::Closure { env, fn_name } => (fn_name.clone(), Some(env.clone())),
        other => {
            set_exception(vm_ctx,
                Value::Str(format!("CallIndirect: expected FuncRef or Closure, got {:?}", other)));
            return 1;
        }
    };

    // 2) Gather args, prepending env when a closure was invoked.
    let user_regs = std::slice::from_raw_parts(args_ptr, args_len);
    let mut args: Vec<Value> = Vec::with_capacity(args_len + env_opt.is_some() as usize);
    if let Some(env) = env_opt {
        args.push(Value::Array(env));
    }
    for &r in user_regs {
        args.push(frame_ref.regs[r as usize].clone());
    }

    // 3) Lookup the callee in the JIT module's compiled function table.
    let entry: &FnEntry = match ctx_ref.fn_entries.get(fn_name.as_str()) {
        Some(e) => e,
        None => {
            set_exception(vm_ctx,
                Value::Str(format!("CallIndirect: undefined function `{}`", fn_name)));
            return 1;
        }
    };

    // 4) Build callee frame, register for GC root scanning, invoke, unregister.
    let mut callee_frame = JitFrame::new(entry.max_reg, &args);
    let jit_fn: JitFn = std::mem::transmute(entry.ptr);
    vm_ctx.push_frame_regs(&callee_frame.regs as *const _);
    let result = jit_fn(&mut callee_frame, ctx);
    vm_ctx.pop_frame_regs();
    if result != 0 {
        callee_frame.recycle();
        return 1;
    }
    frame_ref.regs[dst as usize] = callee_frame.ret.take().unwrap_or(Value::Null);
    callee_frame.recycle();
    0
}
