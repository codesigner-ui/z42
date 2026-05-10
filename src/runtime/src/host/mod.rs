//! Embedding / Hosting API — Tier 1 C ABI implementation.
//!
//! Spec: docs/design/embedding.md
//!       spec/changes/add-embedding-api/
//!
//! H1 scope: lifecycle (initialize / shutdown / set_*_sink / last_error).
//! load_zbc / resolve_entry / invoke return ERR_INTERNAL with an "H2 not
//! yet implemented" message; the contract is locked so H2 can plug real
//! loading + dispatch behind these signatures.
//!
//! Sibling: `crate::native` (interop — native code registering types into z42).

pub mod config;
pub mod entry;
pub mod error;
pub mod module;
pub mod state;

use std::os::raw::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

use z42_abi::Z42Error;

use self::config::Z42HostConfig;
use self::entry::Z42Entry;
use self::error::{clear_error, set_error, snapshot_last_error, Z42HostStatus};
use self::module::Z42Module;
use self::state::{is_valid_handle, HOST_SENTINEL};

/// Opaque pointee for `Z42HostRef`. Pointers are sentinel values; the
/// real state lives in `state::HOST`.
#[repr(C)]
pub struct Z42Host {
    _private: [u8; 0],
}

/// Wrap an extern "C" function body so a Rust panic becomes
/// `Z42_HOST_ERR_INTERNAL` with a stable message rather than UB.
fn guard<F>(f: F) -> Z42HostStatus
where
    F: FnOnce() -> Z42HostStatus,
{
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(status) => status,
        Err(_) => set_error(
            Z42HostStatus::Internal,
            "z42_host: internal panic crossed FFI boundary",
        ),
    }
}

// ── Lifecycle ────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn z42_host_initialize(
    cfg: *const Z42HostConfig,
    out_host: *mut *mut Z42Host,
) -> Z42HostStatus {
    guard(|| {
        if out_host.is_null() {
            return set_error(
                Z42HostStatus::BadConfig,
                "z42_host_initialize: out_host pointer is NULL",
            );
        }
        // Set out_host to NULL first so callers reading on early return
        // see a deterministic state.
        unsafe { *out_host = ptr::null_mut() };

        let resolved = match unsafe { config::validate(cfg) } {
            Ok(r) => r,
            Err(e) => {
                return classify_config_error(e);
            }
        };

        match state::try_initialize(resolved) {
            state::InitOutcome::Initialized => {
                unsafe { *out_host = HOST_SENTINEL as *mut Z42Host };
                clear_error();
                Z42HostStatus::Ok
            }
            state::InitOutcome::AlreadyInit => set_error(
                Z42HostStatus::AlreadyInit,
                "z42_host_initialize: VM is already initialized",
            ),
            state::InitOutcome::LockPoisoned => set_error(
                Z42HostStatus::Internal,
                "z42_host_initialize: host state lock poisoned",
            ),
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn z42_host_shutdown(host: *mut Z42Host) -> Z42HostStatus {
    guard(|| {
        if host.is_null() {
            return set_error(
                Z42HostStatus::BadConfig,
                "z42_host_shutdown: host handle is NULL",
            );
        }
        match state::try_shutdown() {
            state::ShutdownOutcome::Done => {
                clear_error();
                Z42HostStatus::Ok
            }
            state::ShutdownOutcome::NotInit => set_error(
                Z42HostStatus::NotInit,
                "z42_host_shutdown: VM was not initialized",
            ),
            state::ShutdownOutcome::LockPoisoned => set_error(
                Z42HostStatus::Internal,
                "z42_host_shutdown: host state lock poisoned",
            ),
        }
    })
}

// ── Module / entry / invoke (H1: ERR_INTERNAL placeholders) ─────────────

#[no_mangle]
pub unsafe extern "C" fn z42_host_load_zbc(
    host: *mut Z42Host,
    bytes: *const u8,
    length: usize,
    out_module: *mut *mut Z42Module,
) -> Z42HostStatus {
    guard(|| {
        if !is_valid_handle(host as *const ()) {
            return set_error(
                Z42HostStatus::NotInit,
                "z42_host_load_zbc: VM is not initialized",
            );
        }
        if !out_module.is_null() {
            unsafe { *out_module = ptr::null_mut() };
        }
        let _ = (bytes, length);
        set_error(
            Z42HostStatus::Internal,
            "z42_host_load_zbc: H2 not yet implemented",
        )
    })
}

#[no_mangle]
pub unsafe extern "C" fn z42_host_resolve_entry(
    host: *mut Z42Host,
    module: *mut Z42Module,
    fqn: *const std::os::raw::c_char,
    out_entry: *mut *mut Z42Entry,
) -> Z42HostStatus {
    guard(|| {
        if !is_valid_handle(host as *const ()) {
            return set_error(
                Z42HostStatus::NotInit,
                "z42_host_resolve_entry: VM is not initialized",
            );
        }
        if !out_entry.is_null() {
            unsafe { *out_entry = ptr::null_mut() };
        }
        let _ = (module, fqn);
        set_error(
            Z42HostStatus::Internal,
            "z42_host_resolve_entry: H2 not yet implemented",
        )
    })
}

#[no_mangle]
pub unsafe extern "C" fn z42_host_invoke(
    entry: *mut Z42Entry,
    args: *const z42_abi::Z42Value,
    n: usize,
    out_result: *mut z42_abi::Z42Value,
) -> Z42HostStatus {
    guard(|| {
        let _ = (entry, args, n, out_result);
        set_error(
            Z42HostStatus::Internal,
            "z42_host_invoke: H2 not yet implemented",
        )
    })
}

// ── Sinks (H1: store on state; VM stdout wiring lands in H2) ────────────

#[no_mangle]
pub unsafe extern "C" fn z42_host_set_stdout_sink(
    host: *mut Z42Host,
    sink: config::Z42WriteSink,
    user_data: *mut c_void,
) -> Z42HostStatus {
    guard(|| set_sink(host, sink, user_data, SinkSlot::Stdout))
}

#[no_mangle]
pub unsafe extern "C" fn z42_host_set_stderr_sink(
    host: *mut Z42Host,
    sink: config::Z42WriteSink,
    user_data: *mut c_void,
) -> Z42HostStatus {
    guard(|| set_sink(host, sink, user_data, SinkSlot::Stderr))
}

enum SinkSlot {
    Stdout,
    Stderr,
}

fn set_sink(
    host: *mut Z42Host,
    sink: config::Z42WriteSink,
    user_data: *mut c_void,
    slot: SinkSlot,
) -> Z42HostStatus {
    if !is_valid_handle(host as *const ()) {
        let label = match slot {
            SinkSlot::Stdout => "stdout",
            SinkSlot::Stderr => "stderr",
        };
        return set_error(
            Z42HostStatus::NotInit,
            format!("z42_host_set_{label}_sink: VM is not initialized"),
        );
    }
    // H1: just acknowledge. VM-side wiring lands in H2 alongside load_zbc.
    let _ = (sink, user_data);
    clear_error();
    Z42HostStatus::Ok
}

// ── Last error ──────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn z42_host_last_error(_host: *mut Z42Host) -> Z42Error {
    // Reading last-error must not clear it — host may inspect after a
    // failure on multiple call sites. Also intentionally tolerant of a
    // NULL host pointer so callers can read errors from a failed
    // initialize that never produced a handle.
    snapshot_last_error()
}

// ── Helpers ─────────────────────────────────────────────────────────────

fn classify_config_error(e: config::ConfigError) -> Z42HostStatus {
    use config::ConfigError as CE;
    match e {
        CE::NullConfig => set_error(
            Z42HostStatus::BadConfig,
            "z42_host_initialize: cfg pointer is NULL",
        ),
        CE::AbiVersionMismatch { expected, got } => set_error(
            Z42HostStatus::BadConfig,
            format!("z42_host_initialize: abi_version mismatch (expected {expected}, got {got})"),
        ),
        CE::UnknownExecMode { raw } => set_error(
            Z42HostStatus::BadConfig,
            format!("z42_host_initialize: unknown exec_mode {raw}"),
        ),
        CE::FeatureOff { mode } => set_error(
            Z42HostStatus::FeatureOff,
            format!("z42_host_initialize: exec_mode {mode:?} requires a feature disabled at compile time"),
        ),
        CE::BadSearchPath { reason } => set_error(
            Z42HostStatus::BadConfig,
            format!("z42_host_initialize: bad search_paths ({reason})"),
        ),
    }
}

#[cfg(test)]
mod host_tests;
