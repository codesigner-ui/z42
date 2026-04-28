//! Exception runtime — z42 exception object layout and propagation model.
//!
//! **STATUS: STUB** — no public API yet.
//!
//! Phase 1 reality (post extend-jit-helper-abi, 2026-04-28):
//! - **interp** propagates exceptions via `ExecOutcome::Thrown(Value)` enum,
//!   with no thread_local intermediary.
//! - **JIT** stores the in-flight exception in `VmContext::pending_exception`
//!   directly. Helper extern "C" functions all carry `*const JitModuleCtx` as
//!   2nd parameter and reach `VmContext` via `(*jit_ctx).vm_ctx`. No
//!   thread_local exception state remains.
//!
//! All previously-existing thread_local exception slots
//! (`interp::PENDING_EXCEPTION`, `jit::helpers::PENDING_EXCEPTION`) and
//! the `UserException` sentinel + `user_throw` / `user_exception_take` /
//! `sync_in_from_ctx` / `sync_out_to_ctx` bridges are gone (review2 §5.5
//! closed; review2 §3 fully tackled).
//!
//! **Future work**: this module can host the unified exception type hierarchy
//! + cross-backend unwinding contract that L2 / L3 needs (Result<T, E>,
//! cancellation tokens, etc.).
