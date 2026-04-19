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
        JitFrame { regs, ret: None }
    }

    /// Return the register Vec to the pool for reuse.
    pub fn recycle(self) {
        return_pooled_regs(self.regs);
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
pub struct FnEntry {
    /// Pointer to the native machine code of the function.
    pub ptr:     *const u8,
    /// Size of the register file needed by this function (`max_reg`).
    pub max_reg: usize,
}

// Raw pointer — the JITModule that owns the code lives alongside this entry.
unsafe impl Send for FnEntry {}
unsafe impl Sync for FnEntry {}

// ── JitModuleCtx ─────────────────────────────────────────────────────────────

/// Immutable module-level context threaded through every JIT call.
pub struct JitModuleCtx {
    /// Interned string constants (mirrors `Module::string_pool`).
    pub string_pool: Vec<String>,
    /// Compiled function table — name → native code entry.
    pub fn_entries:  HashMap<String, FnEntry>,
    /// Back-pointer to the bytecode module for class descriptors, etc.
    /// SAFETY: the Module must outlive this ctx.
    pub module:      *const crate::metadata::Module,
}

// SAFETY: raw pointer — caller ensures Module outlives ctx.
unsafe impl Send for JitModuleCtx {}
unsafe impl Sync for JitModuleCtx {}
