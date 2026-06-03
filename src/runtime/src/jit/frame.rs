/// JIT frame and module context types.
///
/// `JitFrame` is the runtime stack frame passed (as a raw pointer) to every
/// JIT-compiled function.  `JitModuleCtx` is the read-only module-level context
/// that is shared across all calls within a single module execution.

use crate::metadata::Value;
use std::cell::RefCell;
use std::collections::HashMap;

// ── JitFrame ─────────────────────────────────────────────────────────────────

/// Runtime stack frame for a JIT-compiled function.
/// Pure register machine — all variables use integer register IDs, no named slots.
pub struct JitFrame {
    /// Register file indexed by SSA register number.
    pub regs: Vec<Value>,
    /// Return value written by `jit_set_ret` before the function returns.
    pub ret:  Option<Value>,
    /// 2026-05-02 impl-closure-l3-escape-stack: frame-local arena for
    /// non-escaping closure envs. `Value::StackClosure { env_idx }` indexes
    /// here. Released as part of `JitFrame::recycle` (envs hold normal Drop
    /// semantics — GcRef contents inside env Vec follow their own RC chains).
    pub env_arena: Vec<Vec<Value>>,
}

impl JitFrame {
    /// Allocate a new frame with `max_reg + 1` register slots.
    /// The first `args.len()` registers are initialised with the call arguments.
    pub fn new(max_reg: usize, args: &[Value]) -> Self {
        let size = max_reg + 1;
        let mut regs = take_pooled_regs(size);
        for (i, v) in args.iter().enumerate() {
            if i < size {
                regs[i] = v.clone();
            }
        }
        JitFrame { regs, ret: None, env_arena: Vec::new() }
    }

    /// Return the register Vec to the pool for reuse.
    pub fn recycle(self) {
        return_pooled_regs(self.regs);
        // env_arena drops naturally with `self`; no explicit recycle (pool
        // dimension is reg vector only — env arenas are infrequent and
        // small enough to skip pooling for v1).
    }
}

// ── Frame pool ──────────────────────────────────────────────────────────────

const POOL_MAX: usize = 32;

thread_local! {
    static FRAME_POOL: RefCell<Vec<Vec<Value>>> = const { RefCell::new(Vec::new()) };
}

/// Take a Vec<Value> from the pool (or allocate a new one), sized to `size`.
fn take_pooled_regs(size: usize) -> Vec<Value> {
    FRAME_POOL.with(|pool| {
        let mut pool = pool.borrow_mut();
        if let Some(mut regs) = pool.pop() {
            // Reset to Null and resize
            for v in regs.iter_mut() { *v = Value::Null; }
            regs.resize(size, Value::Null);
            regs
        } else {
            vec![Value::Null; size]
        }
    })
}

/// Return a Vec<Value> to the pool for future reuse.
fn return_pooled_regs(mut regs: Vec<Value>) {
    // Drop all values to release Rc references before pooling
    for v in regs.iter_mut() { *v = Value::Null; }
    FRAME_POOL.with(|pool| {
        let mut pool = pool.borrow_mut();
        if pool.len() < POOL_MAX {
            pool.push(regs);
        }
        // else: drop regs (pool is full)
    });
}

// ── FnEntry ──────────────────────────────────────────────────────────────────

/// A compiled native function entry inside the JIT module.
///
/// `Clone` (no longer `Copy`) since we now carry `Arc<str>` for name + file
/// to give `jit_call` / `jit_vcall` cheap access to the callee's stack-trace
/// metadata without reverse lookup into `module.functions`. Clone cost is
/// two `Arc::clone` (refcount bump) — negligible vs. the JIT call itself.
///
/// (2026-05-10 jit-stack-trace; was `Copy` since introduce-method-token
/// Phase 2.C / 2026-05-08.)
#[derive(Clone)]
pub struct FnEntry {
    /// Pointer to the native machine code of the function.
    pub ptr:     *const u8,
    /// Size of the register file needed by this function (`max_reg`).
    pub max_reg: usize,
    /// Fully-qualified function name (e.g. `"Demo.Inner"`), shared via Arc
    /// across all FnEntry copies. Used to push a `FrameInfo` onto
    /// `VmContext.call_stack` when the JIT invokes this function.
    pub name:    std::sync::Arc<str>,
    /// Source file path (from the function's first `LineEntry`). Empty
    /// `Arc<str>` if the line table omits file references.
    pub file:    std::sync::Arc<str>,
}

// Raw pointer — the JITModule that owns the code lives alongside this entry.
unsafe impl Send for FnEntry {}
unsafe impl Sync for FnEntry {}

// ── JitModuleCtx ─────────────────────────────────────────────────────────────

/// Immutable module-level context threaded through every JIT call.
pub struct JitModuleCtx {
    /// Interned string constants (mirrors `Module::interned_strings`).
    /// review.md C3 Phase 1 (2026-06-03, add-string-literal-interning-phase1):
    /// pre-interned `Arc<str>` per pool slot; `jit_const_str` clones the
    /// Arc (atomic refcount inc, zero alloc) instead of the prior
    /// `String.clone() + .into::<Arc<str>>()` two-alloc path.
    pub string_pool: Vec<std::sync::Arc<str>>,
    /// Compiled function table — name → native code entry.
    pub fn_entries:  HashMap<String, FnEntry>,
    /// Compiled function table — `MethodId.0` → native code entry
    /// (introduce-method-token Phase 2.C, 2026-05-08). Indexed in
    /// `module.functions` order so MethodId matches.
    /// `None` slot = function couldn't be JIT-compiled (e.g. abstract,
    /// extern stub) — caller must handle by falling back to the by-name
    /// HashMap or returning an error. In current builds every function
    /// in `module.functions` gets a JIT entry, so all slots are `Some`.
    pub fn_entries_by_id: Vec<Option<FnEntry>>,
    /// Back-pointer to the bytecode module for class descriptors, etc.
    /// SAFETY: the Module must outlive this ctx.
    pub module:      *const crate::metadata::Module,
    /// Mutable VM state (static fields, pending exception, lazy loader).
    /// Set by `JitModule::run` for the duration of one entry-point invocation;
    /// reset to null on return. JIT helpers reach mutable VM state via this
    /// pointer — replaces the previous `thread_local!` slots in
    /// `jit/helpers.rs` (consolidate-vm-state, 2026-04-28).
    /// SAFETY: the VmContext must outlive `JitModule::run` and be unique
    /// (no concurrent JIT entry on the same JitModule).
    pub vm_ctx:      *mut crate::vm_context::VmContext,
}

// SAFETY: raw pointer — caller ensures Module outlives ctx.
unsafe impl Send for JitModuleCtx {}
unsafe impl Sync for JitModuleCtx {}

/// Byte offset of `JitModuleCtx.vm_ctx` for use in JIT-emitted code that
/// loads the `*mut VmContext` pointer directly from the ctx parameter
/// without going through a helper. Used by inline-jit-safepoint-check
/// (2026-06-03) to reach `vm_ctx.safepoint_skip` from emitted Cranelift IR.
///
/// `offset_of!` is compile-time-evaluated and stable regardless of Rust's
/// field-reordering optimisations. `#[repr(Rust)]` (the default) does not
/// pin field order, but `offset_of!` always reports the actual layout
/// chosen for *this* build, so the emitted code stays correct under any
/// reordering. Asserted in compile-time tests below.
pub const JIT_MODULE_CTX_VM_CTX_OFFSET: i32 =
    std::mem::offset_of!(JitModuleCtx, vm_ctx) as i32;

#[cfg(test)]
mod ctx_offset_tests {
    use super::*;

    /// Offset must fit i32 (Cranelift load/iadd_imm operand width). With
    /// the current 5-field layout the offset is well under 256, but a
    /// future bloat (e.g. inlining caches into ctx) could push it past
    /// i32. Tripping this assert prompts a redesign (e.g. pin the field
    /// near the start of the struct with `#[repr(C)]`).
    #[test]
    fn vm_ctx_offset_fits_i32() {
        let off = std::mem::offset_of!(JitModuleCtx, vm_ctx);
        assert!(off < i32::MAX as usize,
            "JitModuleCtx.vm_ctx offset {off} exceeds i32::MAX — \
             reorder fields or use repr(C) to pin layout");
    }
}
