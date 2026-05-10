//! Embedding / Hosting API — Tier 1 C ABI implementation.
//!
//! Spec: docs/design/embedding.md
//!       spec/archive/2026-05-10-add-embedding-api/
//!
//! H1 scope: lifecycle (initialize / shutdown / sinks / last_error).
//! H2 scope: load_zbc / resolve_entry / invoke + stdout sink VM wiring.
//!
//! Sibling: `crate::native` (interop — native code registering types into z42).

pub mod config;
pub mod entry;
pub mod error;
pub mod marshal;
pub mod module;
pub mod ops;
pub mod state;

use std::os::raw::{c_char, c_void};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

use z42_abi::{Z42Error, Z42Value};

use crate::corelib::io;

use self::config::Z42HostConfig;
use self::entry::Z42Entry;
use self::error::{clear_error, set_error, snapshot_last_error, Z42HostStatus};
use self::module::Z42Module;
use self::state::{is_valid_handle, HostEntry, HOST_SENTINEL};

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

// ── Handle encoding ──────────────────────────────────────────────────────

// `Z42ModuleRef` / `Z42EntryRef` opaque pointers carry an index into the
// host's modules / entries vector encoded as `idx + 1`. Plus-one keeps
// NULL distinct from the first valid handle. v0.1 has no generational
// invalidation — shutdown wipes the singleton, after which any host API
// call returns ERR_NOT_INIT regardless of stale handles.

fn module_handle_to_idx(p: *mut Z42Module) -> Option<usize> {
    let raw = p as usize;
    if raw == 0 {
        None
    } else {
        Some(raw - 1)
    }
}

fn idx_to_module_handle(idx: usize) -> *mut Z42Module {
    (idx + 1) as *mut Z42Module
}

fn entry_handle_to_idx(p: *mut Z42Entry) -> Option<usize> {
    let raw = p as usize;
    if raw == 0 {
        None
    } else {
        Some(raw - 1)
    }
}

fn idx_to_entry_handle(idx: usize) -> *mut Z42Entry {
    (idx + 1) as *mut Z42Entry
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
        unsafe { *out_host = ptr::null_mut() };

        let resolved = match unsafe { config::validate(cfg) } {
            Ok(r) => r,
            Err(e) => return classify_config_error(e),
        };

        // Install host-side sinks (if any) before signalling success so
        // subsequent invokes route through them.
        let stdout_sink = build_host_sink(resolved.stdout_sink, resolved.sink_user_data);
        let stderr_sink = build_host_sink(resolved.stderr_sink, resolved.sink_user_data);
        io::install_host_stdout_sink(stdout_sink);
        io::install_host_stderr_sink(stderr_sink);

        // Probe search_paths for z42.core.zpkg — best-effort, missing
        // corelib is reported only when actual user code references it.
        let corelib = match ops::probe_corelib(&resolved) {
            Ok(c) => c,
            Err(e) => {
                io::install_host_stdout_sink(None);
                io::install_host_stderr_sink(None);
                return set_error(
                    Z42HostStatus::BadConfig,
                    format!("z42_host_initialize: corelib probe failed: {e:#}"),
                );
            }
        };

        match state::try_initialize(resolved, corelib) {
            state::InitOutcome::Initialized => {
                unsafe { *out_host = HOST_SENTINEL as *mut Z42Host };
                clear_error();
                Z42HostStatus::Ok
            }
            state::InitOutcome::AlreadyInit => {
                io::install_host_stdout_sink(None);
                io::install_host_stderr_sink(None);
                set_error(
                    Z42HostStatus::AlreadyInit,
                    "z42_host_initialize: VM is already initialized",
                )
            }
            state::InitOutcome::LockPoisoned => {
                io::install_host_stdout_sink(None);
                io::install_host_stderr_sink(None);
                set_error(
                    Z42HostStatus::Internal,
                    "z42_host_initialize: host state lock poisoned",
                )
            }
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
                io::install_host_stdout_sink(None);
                io::install_host_stderr_sink(None);
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

// ── Module / entry / invoke ─────────────────────────────────────────────

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
        if bytes.is_null() && length != 0 {
            return set_error(
                Z42HostStatus::BadZbc,
                "z42_host_load_zbc: bytes pointer is NULL but length is non-zero",
            );
        }

        let slice = if length == 0 {
            &[][..]
        } else {
            unsafe { std::slice::from_raw_parts(bytes, length) }
        };

        // Build the merged module + ctx outside the write lock so we do
        // I/O without blocking other readers.
        let host_module = match state::with_state_read(|s| ops::build_host_module(slice, s.corelib.as_ref())) {
            Some(Ok(m)) => m,
            Some(Err(e)) => {
                return set_error(
                    Z42HostStatus::BadZbc,
                    format!("z42_host_load_zbc: {e:#}"),
                );
            }
            None => {
                return set_error(
                    Z42HostStatus::NotInit,
                    "z42_host_load_zbc: host state vanished mid-call",
                );
            }
        };

        let idx = match state::with_state_write(|s| {
            s.modules.push(host_module);
            s.modules.len() - 1
        }) {
            Some(i) => i,
            None => {
                return set_error(
                    Z42HostStatus::NotInit,
                    "z42_host_load_zbc: host state vanished mid-call",
                );
            }
        };

        if !out_module.is_null() {
            unsafe { *out_module = idx_to_module_handle(idx) };
        }
        clear_error();
        Z42HostStatus::Ok
    })
}

#[no_mangle]
pub unsafe extern "C" fn z42_host_resolve_entry(
    host: *mut Z42Host,
    module: *mut Z42Module,
    fqn: *const c_char,
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
        if fqn.is_null() {
            return set_error(
                Z42HostStatus::BadConfig,
                "z42_host_resolve_entry: fqn pointer is NULL",
            );
        }
        let module_idx = match module_handle_to_idx(module) {
            Some(i) => i,
            None => {
                return set_error(
                    Z42HostStatus::EntryNotFound,
                    "z42_host_resolve_entry: module handle is NULL",
                );
            }
        };
        let fqn_str = match unsafe { std::ffi::CStr::from_ptr(fqn) }.to_str() {
            Ok(s) => s.to_owned(),
            Err(_) => {
                return set_error(
                    Z42HostStatus::BadConfig,
                    "z42_host_resolve_entry: fqn is not valid UTF-8",
                );
            }
        };

        let lookup = state::with_state_read(|s| -> Result<usize, String> {
            let m = s
                .modules
                .get(module_idx)
                .ok_or_else(|| "module handle is stale or out of range".to_string())?;
            ops::resolve_fqn(&m.module, &fqn_str).map_err(|e| format!("{e:#}"))
        });

        let fn_idx = match lookup {
            Some(Ok(i)) => i,
            Some(Err(msg)) => {
                let status = if msg.contains("not found") {
                    Z42HostStatus::EntryNotFound
                } else {
                    Z42HostStatus::EntryNotFound
                };
                return set_error(status, format!("z42_host_resolve_entry: {msg}"));
            }
            None => {
                return set_error(
                    Z42HostStatus::NotInit,
                    "z42_host_resolve_entry: host state vanished mid-call",
                );
            }
        };

        let entry_idx = match state::with_state_write(|s| {
            s.entries.push(HostEntry {
                module_idx,
                fn_idx,
            });
            s.entries.len() - 1
        }) {
            Some(i) => i,
            None => {
                return set_error(
                    Z42HostStatus::NotInit,
                    "z42_host_resolve_entry: host state vanished mid-call",
                );
            }
        };

        if !out_entry.is_null() {
            unsafe { *out_entry = idx_to_entry_handle(entry_idx) };
        }
        clear_error();
        Z42HostStatus::Ok
    })
}

#[no_mangle]
pub unsafe extern "C" fn z42_host_invoke(
    entry: *mut Z42Entry,
    args: *const Z42Value,
    n: usize,
    out_result: *mut Z42Value,
) -> Z42HostStatus {
    guard(|| {
        if !state::is_initialized() {
            return set_error(
                Z42HostStatus::NotInit,
                "z42_host_invoke: VM is not initialized",
            );
        }
        let entry_idx = match entry_handle_to_idx(entry) {
            Some(i) => i,
            None => {
                return set_error(
                    Z42HostStatus::EntryNotFound,
                    "z42_host_invoke: entry handle is NULL",
                );
            }
        };
        if args.is_null() && n != 0 {
            return set_error(
                Z42HostStatus::ArgMismatch,
                "z42_host_invoke: args pointer is NULL but n is non-zero",
            );
        }
        let args_slice: &[Z42Value] = if n == 0 {
            &[]
        } else {
            unsafe { std::slice::from_raw_parts(args, n) }
        };

        // Marshal args outside any lock so we don't hold the read guard
        // during user code execution.
        let runtime_args = match ops::marshal_args(args_slice) {
            Ok(v) => v,
            Err(e) => {
                return set_error(
                    Z42HostStatus::ArgMismatch,
                    format!("z42_host_invoke: {e:#}"),
                );
            }
        };

        // Resolve module + entry and run; keep the read guard for the
        // execution because interp::run_returning only needs `&` access.
        let outcome = state::with_state_read(|s| {
            let entry = s.entries.get(entry_idx).ok_or_else(|| {
                anyhow::anyhow!("entry handle is stale or out of range")
            })?;
            let host_module = s.modules.get(entry.module_idx).ok_or_else(|| {
                anyhow::anyhow!("module backing this entry is missing")
            })?;
            ops::invoke_impl(host_module, entry, &runtime_args)
        });

        match outcome {
            Some(Ok(ret)) => {
                let z = match ops::marshal_return(ret.as_ref()) {
                    Ok(v) => v,
                    Err(e) => {
                        return set_error(
                            Z42HostStatus::ArgMismatch,
                            format!("z42_host_invoke: {e:#}"),
                        );
                    }
                };
                if !out_result.is_null() {
                    unsafe { *out_result = z };
                }
                clear_error();
                Z42HostStatus::Ok
            }
            Some(Err(e)) => {
                let msg = format!("{e:#}");
                let status = classify_invoke_error(&msg);
                set_error(status, format!("z42_host_invoke: {msg}"))
            }
            None => set_error(
                Z42HostStatus::NotInit,
                "z42_host_invoke: host state vanished mid-call",
            ),
        }
    })
}

// ── Sinks ───────────────────────────────────────────────────────────────

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
    let host_sink = build_host_sink(sink, user_data as usize);
    match slot {
        SinkSlot::Stdout => {
            io::install_host_stdout_sink(host_sink);
        }
        SinkSlot::Stderr => {
            io::install_host_stderr_sink(host_sink);
        }
    }
    clear_error();
    Z42HostStatus::Ok
}

fn build_host_sink(sink: config::Z42WriteSink, user_data: usize) -> Option<io::HostSink> {
    sink.map(|callback| io::HostSink {
        callback,
        user_data: user_data as *mut c_void,
    })
}

// ── Last error ──────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn z42_host_last_error(_host: *mut Z42Host) -> Z42Error {
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

/// Map an interpreter / runtime error message to a `Z42HostStatus`.
/// Heuristic: the interpreter surfaces uncaught z42 exceptions via
/// `exception::format_uncaught`, which prefixes the message with
/// "uncaught exception:". Anything else — verification failure, type
/// registry crash, etc. — buckets as `Internal`.
///
/// `ArgMismatch` is classified before this function is called (in
/// `ops::invoke_impl`) because it has a structural cause rather than a
/// runtime error string.
fn classify_invoke_error(msg: &str) -> Z42HostStatus {
    if msg.contains("arg-count-mismatch:") {
        Z42HostStatus::ArgMismatch
    } else if msg.contains("uncaught exception") {
        Z42HostStatus::VmException
    } else {
        Z42HostStatus::Internal
    }
}

#[cfg(test)]
mod host_tests;
