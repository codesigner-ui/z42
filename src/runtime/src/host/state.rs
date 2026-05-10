//! Process-singleton VM state for the Embedding API.
//!
//! Spec: docs/design/embedding.md §7 + spec/changes/add-embedding-api/design.md D1/D5.
//!
//! v0.1 holds one optional `HostState` behind an `RwLock`. `initialize`
//! takes the write lock and refuses if state is already `Some`; `shutdown`
//! takes the write lock and replaces with `None`. Multi-instance support
//! is in Deferred — the current shape leaves the public API unchanged.

use std::sync::RwLock;

use super::config::ResolvedConfig;

/// Shape of an in-process VM. H1 holds only configuration; modules /
/// entries / VM-internal handles plug in during H2.
pub(crate) struct HostState {
    #[allow(dead_code)] // consumed in H2
    pub config: ResolvedConfig,
}

// `ResolvedConfig` carries `Z42WriteSink` (function pointer) and a raw
// user-data address stored as `usize`. Function pointers are `Send`/`Sync`
// in Rust; the user_data address has no Rust ownership. Documented in
// embedding.md §7: hosts must guarantee thread safety on their side.
unsafe impl Send for HostState {}
unsafe impl Sync for HostState {}

static HOST: RwLock<Option<HostState>> = RwLock::new(None);

/// Sentinel pointer returned for `Z42HostRef` while the singleton is
/// alive. Non-null and never dereferenced — see `is_valid_handle`.
pub(crate) const HOST_SENTINEL: usize = 0x1;

/// Outcome of `try_initialize`.
pub(crate) enum InitOutcome {
    Initialized,
    AlreadyInit,
    LockPoisoned,
}

/// Atomically transition Uninitialized → Initialized.
pub(crate) fn try_initialize(config: ResolvedConfig) -> InitOutcome {
    let mut guard = match HOST.write() {
        Ok(g) => g,
        Err(_) => return InitOutcome::LockPoisoned,
    };
    if guard.is_some() {
        return InitOutcome::AlreadyInit;
    }
    *guard = Some(HostState { config });
    InitOutcome::Initialized
}

/// Outcome of `try_shutdown`.
pub(crate) enum ShutdownOutcome {
    Done,
    NotInit,
    LockPoisoned,
}

/// Atomically transition Initialized → Uninitialized.
pub(crate) fn try_shutdown() -> ShutdownOutcome {
    let mut guard = match HOST.write() {
        Ok(g) => g,
        Err(_) => return ShutdownOutcome::LockPoisoned,
    };
    if guard.is_none() {
        return ShutdownOutcome::NotInit;
    }
    *guard = None;
    ShutdownOutcome::Done
}

/// Whether the singleton is currently initialized.
pub(crate) fn is_initialized() -> bool {
    matches!(HOST.read(), Ok(g) if g.is_some())
}

/// `true` if `handle` matches the singleton sentinel and the VM is live.
pub(crate) fn is_valid_handle(handle_ptr: *const ()) -> bool {
    !handle_ptr.is_null() && handle_ptr as usize == HOST_SENTINEL && is_initialized()
}

