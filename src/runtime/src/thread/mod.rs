//! Threading support — z42 coroutine / OS-thread abstractions.
//!
//! **STATUS: STUB** — no public API yet.
//!
//! Phase 1 reality (post consolidate-vm-state, 2026-04-28): the VM is still
//! single-threaded but the bulk of mutable state is now owned by an explicit
//! `VmContext` (see `crate::vm_context`). Two `thread_local!` slots survive
//! in `jit/helpers.rs` (PENDING_EXCEPTION + STATIC_FIELDS) as
//! extern-"C"-helper backing store; `JitModule::run` syncs them with
//! `VmContext` at the run boundary so each ctx sees its own state across
//! serial executions. Plus `jit/frame.rs::FRAME_POOL` (allocator cache, not
//! state) — by design per-thread.
//!
//! **Future work**:
//! - Phase A (next P1 spec): extend JIT helper ABI to carry
//!   `*const JitModuleCtx` so the last 2 thread_locals can be deleted; tracked
//!   as `extend-jit-helper-abi`.
//! - Phase B (L3+): introduce coroutine / OS-thread primitives backing the
//!   `async` / `await` syntax already accepted by the parser. Implementation
//!   awaits the GC (`super::gc`) so cross-thread reachability is sound.
