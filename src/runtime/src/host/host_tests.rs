//! Unit tests for the H1 lifecycle path of the embedding API.
//!
//! Spec: spec/changes/add-embedding-api/specs/embedding-host-api/spec.md
//!       (Requirement: 测试覆盖)
//!
//! All tests serialize on `HOST_TEST_LOCK` because the embedded VM is a
//! process singleton — concurrent tests would race on `state::HOST`.

use std::ffi::CStr;
use std::ptr;
use std::sync::Mutex;

use super::config::{Z42HostConfig, Z42_HOST_ABI_VERSION};
use super::error::Z42HostStatus;
use super::*;

/// Serializes all host-API tests in this binary so they don't trip over
/// each other's singleton state.
static HOST_TEST_LOCK: Mutex<()> = Mutex::new(());

/// Acquire the test lock. Poisoning is ignored — a panicking test still
/// leaves us free to run the rest of the suite (singleton is reset by
/// each test's setup phase).
fn test_lock() -> std::sync::MutexGuard<'static, ()> {
    match HOST_TEST_LOCK.lock() {
        Ok(g) => g,
        Err(p) => p.into_inner(),
    }
}

/// Force the singleton back to "uninitialized" so a test starts clean.
/// Called at the top of every test; tolerates both "already off" and
/// "leftover from previous test".
fn reset_host() {
    // Repeated shutdowns are fine; second one returns NotInit which we
    // simply ignore.
    let _ = unsafe { z42_host_shutdown(state::HOST_SENTINEL as *mut Z42Host) };
}

fn default_config() -> Z42HostConfig {
    Z42HostConfig {
        abi_version: Z42_HOST_ABI_VERSION,
        reserved: 0,
        exec_mode: config::Z42ExecMode::Interp as i32,
        heap_initial_bytes: 0,
        heap_max_bytes: 0,
        stdout_sink: None,
        stderr_sink: None,
        sink_user_data: ptr::null_mut(),
        search_paths: ptr::null(),
    }
}

#[test]
fn initialize_then_shutdown() {
    let _g = test_lock();
    reset_host();

    let cfg = default_config();
    let mut handle: *mut Z42Host = ptr::null_mut();
    let status = unsafe { z42_host_initialize(&cfg, &mut handle) };
    assert_eq!(status, Z42HostStatus::Ok);
    assert!(!handle.is_null());

    let status = unsafe { z42_host_shutdown(handle) };
    assert_eq!(status, Z42HostStatus::Ok);
}

#[test]
fn initialize_twice_returns_already_init() {
    let _g = test_lock();
    reset_host();

    let cfg = default_config();
    let mut h1: *mut Z42Host = ptr::null_mut();
    assert_eq!(
        unsafe { z42_host_initialize(&cfg, &mut h1) },
        Z42HostStatus::Ok
    );

    let mut h2: *mut Z42Host = ptr::null_mut();
    let status = unsafe { z42_host_initialize(&cfg, &mut h2) };
    assert_eq!(status, Z42HostStatus::AlreadyInit);
    assert!(h2.is_null(), "second initialize must not produce a handle");

    // Cleanup so subsequent tests start clean.
    assert_eq!(
        unsafe { z42_host_shutdown(h1) },
        Z42HostStatus::Ok
    );
}

#[test]
fn shutdown_then_reinitialize() {
    let _g = test_lock();
    reset_host();

    let cfg = default_config();
    let mut h: *mut Z42Host = ptr::null_mut();
    assert_eq!(
        unsafe { z42_host_initialize(&cfg, &mut h) },
        Z42HostStatus::Ok
    );
    assert_eq!(
        unsafe { z42_host_shutdown(h) },
        Z42HostStatus::Ok
    );

    // After shutdown, a fresh initialize must succeed.
    let mut h2: *mut Z42Host = ptr::null_mut();
    assert_eq!(
        unsafe { z42_host_initialize(&cfg, &mut h2) },
        Z42HostStatus::Ok
    );
    assert!(!h2.is_null());

    assert_eq!(
        unsafe { z42_host_shutdown(h2) },
        Z42HostStatus::Ok
    );
}

#[test]
fn shutdown_when_not_initialized_returns_not_init() {
    let _g = test_lock();
    reset_host();

    let status = unsafe { z42_host_shutdown(state::HOST_SENTINEL as *mut Z42Host) };
    assert_eq!(status, Z42HostStatus::NotInit);
}

#[test]
fn null_config_returns_bad_config() {
    let _g = test_lock();
    reset_host();

    let mut handle: *mut Z42Host = ptr::null_mut();
    let status = unsafe { z42_host_initialize(ptr::null(), &mut handle) };
    assert_eq!(status, Z42HostStatus::BadConfig);
    assert!(handle.is_null());
}

#[test]
fn bad_abi_version_returns_bad_config() {
    let _g = test_lock();
    reset_host();

    let mut cfg = default_config();
    cfg.abi_version = 999;

    let mut handle: *mut Z42Host = ptr::null_mut();
    let status = unsafe { z42_host_initialize(&cfg, &mut handle) };
    assert_eq!(status, Z42HostStatus::BadConfig);
    assert!(handle.is_null());

    let err = unsafe { z42_host_last_error(ptr::null_mut()) };
    assert_ne!(err.code, 0);
    let msg = unsafe { CStr::from_ptr(err.message) }.to_string_lossy();
    assert!(
        msg.contains("abi_version"),
        "expected abi_version detail, got {msg}"
    );
}

#[test]
fn last_error_clears_on_success() {
    let _g = test_lock();
    reset_host();

    // Trigger a failure to populate last_error.
    let mut cfg = default_config();
    cfg.abi_version = 999;
    let mut h: *mut Z42Host = ptr::null_mut();
    let _ = unsafe { z42_host_initialize(&cfg, &mut h) };
    assert_ne!(
        unsafe { z42_host_last_error(ptr::null_mut()) }.code,
        0,
        "precondition: last_error should be set after the failed init"
    );

    // Now do a successful initialize and assert the slot was cleared.
    cfg.abi_version = Z42_HOST_ABI_VERSION;
    assert_eq!(
        unsafe { z42_host_initialize(&cfg, &mut h) },
        Z42HostStatus::Ok
    );
    assert_eq!(
        unsafe { z42_host_last_error(ptr::null_mut()) }.code,
        0,
        "successful call must clear thread-local last_error"
    );

    assert_eq!(
        unsafe { z42_host_shutdown(h) },
        Z42HostStatus::Ok
    );
}

#[test]
fn last_error_persists_on_failure() {
    let _g = test_lock();
    reset_host();

    let mut cfg = default_config();
    cfg.abi_version = 42;
    let mut h: *mut Z42Host = ptr::null_mut();
    let status = unsafe { z42_host_initialize(&cfg, &mut h) };
    assert_eq!(status, Z42HostStatus::BadConfig);

    // Read twice; both reads must see the same error without it being
    // implicitly cleared.
    let e1 = unsafe { z42_host_last_error(ptr::null_mut()) };
    let e2 = unsafe { z42_host_last_error(ptr::null_mut()) };
    assert_eq!(e1.code, e2.code);
    assert_ne!(e1.code, 0);

    let msg = unsafe { CStr::from_ptr(e1.message) }.to_string_lossy();
    assert!(msg.contains("abi_version"));
}

// ── Additional H1 coverage ──────────────────────────────────────────────

#[test]
fn unknown_exec_mode_returns_bad_config() {
    let _g = test_lock();
    reset_host();

    let mut cfg = default_config();
    cfg.exec_mode = 7; // not a valid Z42ExecMode discriminator
    let mut h: *mut Z42Host = ptr::null_mut();
    let status = unsafe { z42_host_initialize(&cfg, &mut h) };
    assert_eq!(status, Z42HostStatus::BadConfig);
    assert!(h.is_null());
}

#[test]
fn jit_mode_when_feature_off_returns_feature_off() {
    let _g = test_lock();
    reset_host();

    let mut cfg = default_config();
    cfg.exec_mode = config::Z42ExecMode::Jit as i32;
    let mut h: *mut Z42Host = ptr::null_mut();
    let status = unsafe { z42_host_initialize(&cfg, &mut h) };

    #[cfg(feature = "jit")]
    {
        assert_eq!(status, Z42HostStatus::Ok);
        assert_eq!(
            unsafe { z42_host_shutdown(h) },
            Z42HostStatus::Ok
        );
    }
    #[cfg(not(feature = "jit"))]
    {
        assert_eq!(status, Z42HostStatus::FeatureOff);
        assert!(h.is_null());
    }
}

#[test]
fn load_zbc_before_init_returns_not_init() {
    let _g = test_lock();
    reset_host();

    let mut out: *mut Z42Module = ptr::null_mut();
    let status = unsafe {
        z42_host_load_zbc(
            state::HOST_SENTINEL as *mut Z42Host,
            ptr::null(),
            0,
            &mut out,
        )
    };
    assert_eq!(status, Z42HostStatus::NotInit);
}

#[test]
fn load_zbc_after_init_returns_internal_h2_placeholder() {
    let _g = test_lock();
    reset_host();

    let cfg = default_config();
    let mut h: *mut Z42Host = ptr::null_mut();
    assert_eq!(
        unsafe { z42_host_initialize(&cfg, &mut h) },
        Z42HostStatus::Ok
    );

    let mut out: *mut Z42Module = ptr::null_mut();
    let status = unsafe { z42_host_load_zbc(h, ptr::null(), 0, &mut out) };
    assert_eq!(status, Z42HostStatus::Internal);

    let err = unsafe { z42_host_last_error(ptr::null_mut()) };
    let msg = unsafe { CStr::from_ptr(err.message) }.to_string_lossy();
    assert!(msg.contains("H2"), "placeholder message should mention H2: {msg}");

    assert_eq!(
        unsafe { z42_host_shutdown(h) },
        Z42HostStatus::Ok
    );
}
