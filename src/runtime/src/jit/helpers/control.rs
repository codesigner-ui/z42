#![allow(dangerous_implicit_autorefs)]
//! Exception-flow helpers: `throw`, `install_catch`, `match_catch_type`.
//! Mirror the interpreter's `Terminator::Throw` and `find_handler` paths.

use crate::metadata::Value;
use super::super::frame::{JitFrame, JitModuleCtx};
use super::{set_exception, take_exception, vm_ctx_ref};

#[no_mangle]
pub unsafe extern "C" fn jit_throw(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    reg: u32,
) {
    let v = (*frame).regs[reg as usize].clone();
    set_exception(vm_ctx_ref(ctx), v);
}

#[no_mangle]
pub unsafe extern "C" fn jit_install_catch(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    catch_reg: u32,
) {
    let v = take_exception(vm_ctx_ref(ctx)).unwrap_or(Value::Null);
    (*frame).regs[catch_reg as usize] = v;
}

/// catch-by-generic-type (2026-05-06): peek at the pending exception's runtime
/// class and return 1 if it is `target` (or a subclass) — i.e. matches a
/// `catch (target e)` clause. Returns 0 otherwise (or if there is no pending
/// exception / it is not an Object). The exception is left in place for a
/// later `jit_install_catch` call once a matching handler is selected.
#[no_mangle]
pub unsafe extern "C" fn jit_match_catch_type(
    _frame: *mut JitFrame, ctx: *const JitModuleCtx,
    target_ptr: *const u8, target_len: i64,
) -> i8 {
    let target = match std::str::from_utf8(
        std::slice::from_raw_parts(target_ptr, target_len as usize)) {
        Ok(s)  => s,
        Err(_) => return 0,
    };
    let vm_ctx = vm_ctx_ref(ctx);
    let exc = match vm_ctx.peek_exception() {
        Some(v) => v,
        None    => return 0,
    };
    let derived = match &exc {
        Value::Object(rc) => rc.borrow().type_desc.name.clone(),
        _                 => return 0, // primitives / null don't match typed catches
    };
    let module = &*(*ctx).module;
    if crate::interp::dispatch::is_subclass_or_eq_td(&module.type_registry, &derived, target) {
        1
    } else {
        0
    }
}
