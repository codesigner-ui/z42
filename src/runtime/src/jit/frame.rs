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

    /// Allocate a frame and fill its first registers directly from the caller's
    /// register file, indexed by `arg_indices`. Avoids the intermediate
    /// `Vec<Value>` collect + the resulting double-clone that `new(_, &args)`
    /// incurs on the hot `jit_call` path (perf: per-call malloc/free + one of
    /// two arg clones eliminated; reg Vec still pooled). Each argument is cloned
    /// exactly once (caller reg → callee reg).
    pub fn new_args_from(max_reg: usize, caller_regs: &[Value], arg_indices: &[u32]) -> Self {
        let size = max_reg + 1;
        let mut regs = take_pooled_regs(size);
        for (i, &r) in arg_indices.iter().enumerate() {
            if i < size {
                regs[i] = caller_regs[r as usize].clone();
            }
        }
        JitFrame { regs, ret: None, env_arena: Vec::new() }
    }

    /// Like `new_args_from`, but for a method call: register 0 is the receiver
    /// (`this`), and registers `1..` are the positional args read from the
    /// caller's register file via `arg_indices`. Avoids the
    /// `vec![obj]` + `append(extra_args)` two-Vec dance on the hot `jit_vcall`
    /// path. The receiver is moved in (already cloned by the caller).
    pub fn new_method_args_from(
        max_reg: usize, receiver: Value, caller_regs: &[Value], arg_indices: &[u32],
    ) -> Self {
        let size = max_reg + 1;
        let mut regs = take_pooled_regs(size);
        if size > 0 { regs[0] = receiver; }
        for (i, &r) in arg_indices.iter().enumerate() {
            let slot = i + 1;
            if slot < size {
                regs[slot] = caller_regs[r as usize].clone();
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
///
/// INVARIANT: every Vec in the pool is already all-`Value::Null` (see
/// `return_pooled_regs`, which nulls before pooling; fresh Vecs start Null).
/// So we only `resize` to the requested length — no redundant per-element
/// reset on take (that reset already happened on the matching recycle, and
/// doing it twice is pure per-call overhead on every register slot).
fn take_pooled_regs(size: usize) -> Vec<Value> {
    FRAME_POOL.with(|pool| {
        let mut pool = pool.borrow_mut();
        if let Some(mut regs) = pool.pop() {
            // `regs` is all-Null by the pool invariant; resize keeps it Null
            // (truncated tail is Null → no-op drops; growth fills with Null).
            regs.resize(size, Value::Null);
            regs
        } else {
            vec![Value::Null; size]
        }
    })
}

/// Return a Vec<Value> to the pool for future reuse. Nulls every slot first to
/// release Arc/Rc references promptly AND uphold the all-Null pool invariant
/// relied on by `take_pooled_regs`.
fn return_pooled_regs(mut regs: Vec<Value>) {
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
