//! `#[no_mangle]` `z42_*` ABI entry points called by native libraries.
//!
//! These functions are the C-side surface of Tier 1: native code dlopens
//! the z42 host (or links statically against the test binary) and calls
//! these to register types and invoke z42 methods.
//!
//! The thread-local `CURRENT_VM` slot carries the active `VmContext` while
//! the interpreter is running; native callbacks fired from inside z42
//! find the VM through this slot. See [`VmGuard`] for the RAII wrapper.

use std::cell::Cell;
use std::ffi::CStr;
use std::marker::PhantomData;
use std::os::raw::c_char;
use std::ptr;
use std::rc::Rc;

use z42_abi::{Z42Error, Z42TypeDescriptor_v1, Z42TypeOpaque, Z42TypeRef, Z42Value};

use super::error::{self, Z0905, Z0906};
use super::registry::RegisteredType;
use crate::vm_context::VmContext;

// ── thread_local current-VM pointer ──────────────────────────────────────

thread_local! {
    /// `*const VmContext` for the currently executing z42 interpreter on
    /// this thread. Set on entry to `interp::exec_function` via
    /// [`VmGuard`], cleared on exit (including unwind).
    static CURRENT_VM: Cell<*const VmContext> = const { Cell::new(ptr::null()) };
}

/// RAII guard that scopes a `VmContext` reference into `CURRENT_VM` for
/// the lifetime of `'a`. Used by `interp::exec_function` so any native
/// callback fired during interpretation can locate the VM.
pub struct VmGuard<'a> {
    prev: *const VmContext,
    _phantom: PhantomData<&'a VmContext>,
}

impl<'a> VmGuard<'a> {
    pub fn enter(ctx: &'a VmContext) -> Self {
        let prev = CURRENT_VM.with(|c| c.replace(ctx as *const _));
        VmGuard { prev, _phantom: PhantomData }
    }
}

impl Drop for VmGuard<'_> {
    fn drop(&mut self) {
        CURRENT_VM.with(|c| c.set(self.prev));
    }
}

/// Borrow the VM pointed to by `CURRENT_VM`. Returns `None` if no VM is
/// currently active on this thread.
fn current_vm<'a>() -> Option<&'a VmContext> {
    CURRENT_VM.with(|c| {
        let p = c.get();
        if p.is_null() { None } else { Some(unsafe { &*p }) }
    })
}

// ── Z42TypeRef sentinels ────────────────────────────────────────────────

const NULL_TYPE_REF: Z42TypeRef = Z42TypeRef(ptr::null_mut());

/// `Z42TypeRef` is a stable handle. We treat the raw pointer as a key into
/// a per-VM type table; for C2 we just leak `Rc::into_raw(ty)` so the
/// pointer stays alive as long as the VM holds the `Rc` clone.
fn type_ref_from_rc(ty: &Rc<RegisteredType>) -> Z42TypeRef {
    let ptr = Rc::as_ptr(ty) as *mut Z42TypeOpaque;
    Z42TypeRef(ptr)
}

// ── z42_register_type ────────────────────────────────────────────────────

/// Register a native type with the currently active VM. Returns a non-NULL
/// [`Z42TypeRef`] on success or `NULL` on failure (consult
/// [`z42_last_error`]).
///
/// # Safety
/// Caller must guarantee `desc` is a valid pointer to a
/// `Z42TypeDescriptor_v1` whose strings live for the VM's lifetime.
#[no_mangle]
pub unsafe extern "C" fn z42_register_type(
    desc: *const Z42TypeDescriptor_v1,
) -> Z42TypeRef {
    error::clear();

    let Some(vm) = current_vm() else {
        error::set(Z0905, "z42_register_type called outside an active z42 VM context");
        return NULL_TYPE_REF;
    };

    let registered = match unsafe { RegisteredType::from_descriptor(desc) } {
        Ok(r) => r,
        Err(e) => {
            // Map the anyhow message back to Z0905 vs Z0906 by inspecting prefix.
            let msg = format!("{e:#}");
            let code = if msg.contains("Z0906") { Z0906 } else { Z0905 };
            error::set(code, msg);
            return NULL_TYPE_REF;
        }
    };

    let arc = Rc::new(registered);
    if !vm.register_native_type(arc.clone()) {
        error::set(
            Z0905,
            format!(
                "duplicate native type {}::{}",
                arc.module(),
                arc.type_name()
            ),
        );
        return NULL_TYPE_REF;
    }

    type_ref_from_rc(&arc)
}

// ── z42_resolve_type ─────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn z42_resolve_type(
    module: *const c_char,
    type_name: *const c_char,
) -> Z42TypeRef {
    error::clear();
    let Some(vm) = current_vm() else { return NULL_TYPE_REF; };
    if module.is_null() || type_name.is_null() { return NULL_TYPE_REF; }

    let m = match unsafe { CStr::from_ptr(module) }.to_str() {
        Ok(s) => s,
        Err(_) => return NULL_TYPE_REF,
    };
    let n = match unsafe { CStr::from_ptr(type_name) }.to_str() {
        Ok(s) => s,
        Err(_) => return NULL_TYPE_REF,
    };

    match vm.resolve_native_type(m, n) {
        Some(rc) => type_ref_from_rc(&rc),
        None => NULL_TYPE_REF,
    }
}

// ── z42_invoke (C2 stub: full method dispatch lands when used) ──────────

#[no_mangle]
pub unsafe extern "C" fn z42_invoke(
    _ty: Z42TypeRef,
    _method: *const c_char,
    _args: *const Z42Value,
    _arg_count: usize,
) -> Z42Value {
    // C2 only validates the registry path; reverse-call (native → z42)
    // dispatch is wired in C5 when the source generator emits the calls.
    error::set(Z0905, "z42_invoke not implemented in C2 (lands in spec C5)");
    super::dispatch::z42_null()
}

#[no_mangle]
pub unsafe extern "C" fn z42_invoke_method(
    _receiver: Z42Value,
    _method: *const c_char,
    _args: *const Z42Value,
    _arg_count: usize,
) -> Z42Value {
    error::set(Z0905, "z42_invoke_method not implemented in C2 (lands in spec C5)");
    super::dispatch::z42_null()
}

// ── z42_last_error ───────────────────────────────────────────────────────

#[no_mangle]
pub extern "C" fn z42_last_error() -> Z42Error {
    error::last()
}
