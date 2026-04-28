//! Exception runtime — z42 exception object layout and propagation model.
//!
//! **STATUS: STUB** — no public API yet.
//!
//! Phase 1 reality: exception state is currently held in
//! `interp/mod.rs::PENDING_EXCEPTION` (a `thread_local!` `RefCell<Option<Value>>`)
//! and propagated via `user_throw` / `user_exception_take` through `anyhow::Error`.
//! See review2 §3 + §5.5: this dual mechanism (PENDING_EXCEPTION thread_local +
//! `ExecOutcome::Thrown` enum) is slated for unification.
//!
//! **Future work** (planned P1 refactor; runtime side):
//! - Move all exception state into a `VmContext` struct passed by reference
//! - Delete `PENDING_EXCEPTION` thread_local and `user_throw` helper
//! - All paths use `ExecOutcome::Thrown(Value)` enum exclusively
//! - This module will then host the unified exception type hierarchy +
//!   cross-backend (interp / JIT) unwinding contract.
