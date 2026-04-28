//! Threading support — z42 coroutine / OS-thread abstractions.
//!
//! **STATUS: STUB** — no public API yet.
//!
//! Phase 1 reality: the entire VM is single-threaded. Several pieces of state
//! are held in `thread_local!` slots (`STATIC_FIELDS` in `interp/dispatch.rs`,
//! `PENDING_EXCEPTION` in `interp/mod.rs`, `LazyLoader::STATE` in
//! `metadata/lazy_loader.rs`), which technically isolates them per OS thread —
//! but no z42 user code can spawn additional threads, so the per-thread
//! semantics are vacuous.
//!
//! **Future work**:
//! - Phase A (P1 refactor): replace the 3 thread_local slots with explicit
//!   `&mut VmContext` plumbing so multi-VM-per-process becomes viable
//!   (review2 §3).
//! - Phase B (L3+): introduce coroutine / OS-thread primitives backing the
//!   `async` / `await` syntax already accepted by the parser. Implementation
//!   awaits the GC (`super::gc`) so cross-thread reachability is sound.
