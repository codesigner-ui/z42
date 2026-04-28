//! Exception runtime — z42 exception object layout and propagation model.
//!
//! **STATUS: STUB** — no public API yet.
//!
//! Phase 1 reality (post consolidate-vm-state, 2026-04-28):
//! - **interp** propagates exceptions via `ExecOutcome::Thrown(Value)` enum,
//!   with no thread_local intermediary. The legacy `UserException` sentinel
//!   + `PENDING_EXCEPTION` thread_local + `user_throw` / `user_exception_take`
//!   helpers were deleted — review2 §5.5 closed.
//! - **JIT** stores the in-flight exception in `jit/helpers.rs::PENDING_EXCEPTION`
//!   thread_local for ABI reasons (extern "C" arith/bool helpers don't carry
//!   ctx). `JitModule::run` syncs the slot with `VmContext::pending_exception`
//!   at the entry/exit boundary, so each VmContext sees its own slot across
//!   serial runs.
//!
//! **Future work**:
//! - Extend JIT helper ABI to receive `*const JitModuleCtx` (unblocks deleting
//!   the last JIT thread_local and brings full multi-VM-per-thread parity);
//!   tracked as follow-up `extend-jit-helper-abi` spec.
//! - Once that lands, this module can host the unified exception type
//!   hierarchy + cross-backend unwinding contract that L2 / L3 needs.
