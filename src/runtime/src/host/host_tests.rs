//! Unit + integration tests for the embedding API.
//!
//! Spec: spec/archive/2026-05-10-add-embedding-api/specs/embedding-host-api/spec.md
//!       (Requirement: 测试覆盖)
//!
//! All tests serialize on `HOST_TEST_LOCK` because the embedded VM is a
//! process singleton — concurrent tests would race on `state::HOST`.
//! The end-to-end `load_invoke_hello_world` test is gated on
//! `cfg(z42_have_embedding_hello)` so the suite still builds when
//! `z42c.dll` isn't available (e.g. CI agents that haven't run
//! `dotnet build src/compiler/z42.slnx` yet).

use std::ffi::{CStr, CString};
use std::os::raw::{c_char, c_void};
use std::ptr;
use std::sync::{Mutex, OnceLock};

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

// ── End-to-end hello-world ──────────────────────────────────────────────

/// Captures bytes written through the host stdout sink. Pointer to this
/// struct is the `user_data` for `host_stdout_sink_callback`.
struct StdoutCapture {
    bytes: Mutex<Vec<u8>>,
}

unsafe extern "C" fn host_stdout_sink_callback(
    bytes: *const c_char,
    length: usize,
    user_data: *mut c_void,
) {
    if user_data.is_null() {
        return;
    }
    let capture = unsafe { &*(user_data as *const StdoutCapture) };
    let slice = if bytes.is_null() || length == 0 {
        &[][..]
    } else {
        unsafe { std::slice::from_raw_parts(bytes as *const u8, length) }
    };
    if let Ok(mut guard) = capture.bytes.lock() {
        guard.extend_from_slice(slice);
    }
}

/// Project root under cargo, used to locate `artifacts/z42/libs/`.
fn project_root() -> std::path::PathBuf {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap()
        .to_path_buf()
}

/// `CString` form of the libs dir; `OnceLock` so the search-paths array
/// in `Z42HostConfig` can hold a stable pointer.
fn libs_dir_cstring() -> &'static CString {
    static LIBS_DIR: OnceLock<CString> = OnceLock::new();
    LIBS_DIR.get_or_init(|| {
        let p = project_root().join("artifacts/z42/libs");
        CString::new(p.to_string_lossy().as_bytes()).expect("libs dir contains no NUL")
    })
}

#[test]
#[cfg(z42_have_embedding_hello)]
fn load_invoke_hello_world() {
    let _g = test_lock();
    reset_host();

    // Skip cleanly if `dotnet build` hasn't produced the corelib zpkg.
    let libs_dir = project_root().join("artifacts/z42/libs/z42.core.zpkg");
    if !libs_dir.is_file() {
        eprintln!(
            "skipping load_invoke_hello_world: {} not found (run `dotnet build src/compiler/z42.slnx`)",
            libs_dir.display()
        );
        return;
    }

    let capture = StdoutCapture {
        bytes: Mutex::new(Vec::new()),
    };
    let user_data = &capture as *const StdoutCapture as *mut c_void;

    let libs_path = libs_dir_cstring();
    let search_paths: [*const c_char; 2] = [libs_path.as_ptr(), ptr::null()];

    let cfg = Z42HostConfig {
        abi_version: Z42_HOST_ABI_VERSION,
        reserved: 0,
        exec_mode: config::Z42ExecMode::Interp as i32,
        heap_initial_bytes: 0,
        heap_max_bytes: 0,
        stdout_sink: Some(host_stdout_sink_callback),
        stderr_sink: None,
        sink_user_data: user_data,
        search_paths: search_paths.as_ptr(),
    };

    let mut host: *mut Z42Host = ptr::null_mut();
    assert_eq!(
        unsafe { z42_host_initialize(&cfg, &mut host) },
        Z42HostStatus::Ok,
        "initialize must succeed; last_error={}",
        unsafe { CStr::from_ptr(z42_host_last_error(ptr::null_mut()).message) }.to_string_lossy()
    );
    assert!(!host.is_null());

    let zbc_bytes: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/embedding_hello.zbc"));
    let mut module: *mut Z42Module = ptr::null_mut();
    let load_status = unsafe {
        z42_host_load_zbc(host, zbc_bytes.as_ptr(), zbc_bytes.len(), &mut module)
    };
    assert_eq!(
        load_status,
        Z42HostStatus::Ok,
        "load_zbc must succeed; last_error={}",
        unsafe { CStr::from_ptr(z42_host_last_error(ptr::null_mut()).message) }.to_string_lossy()
    );
    assert!(!module.is_null());

    let fqn = CString::new("Embedding.Hello.Main").unwrap();
    let mut entry: *mut Z42Entry = ptr::null_mut();
    let resolve_status =
        unsafe { z42_host_resolve_entry(host, module, fqn.as_ptr(), &mut entry) };
    assert_eq!(
        resolve_status,
        Z42HostStatus::Ok,
        "resolve_entry must succeed; last_error={}",
        unsafe { CStr::from_ptr(z42_host_last_error(ptr::null_mut()).message) }.to_string_lossy()
    );
    assert!(!entry.is_null());

    let mut result = z42_abi::Z42Value {
        tag: u32::MAX,
        reserved: 0,
        payload: 0,
    };
    let invoke_status = unsafe { z42_host_invoke(entry, ptr::null(), 0, &mut result) };
    assert_eq!(
        invoke_status,
        Z42HostStatus::Ok,
        "invoke must succeed; last_error={}",
        unsafe { CStr::from_ptr(z42_host_last_error(ptr::null_mut()).message) }.to_string_lossy()
    );
    // void return ⇒ NULL-tagged Z42Value.
    assert_eq!(result.tag, z42_abi::Z42_VALUE_TAG_NULL);

    let captured = String::from_utf8_lossy(&capture.bytes.lock().unwrap()).into_owned();
    assert_eq!(
        captured, "Hello, World!\n",
        "host stdout sink must receive the exact line emitted by Console.WriteLine"
    );

    assert_eq!(
        unsafe { z42_host_shutdown(host) },
        Z42HostStatus::Ok
    );
}

// ── H3 error-path coverage ──────────────────────────────────────────────

/// Helper: run a host session against the embedding_hello fixture and
/// hand the closure a fully-initialised `(Host*, Module*)` tuple. The
/// session is shut down on the way out regardless of test outcome.
#[cfg(z42_have_embedding_hello)]
fn with_hello_session<R>(
    capture: &Mutex<Vec<u8>>,
    body: impl FnOnce(*mut Z42Host, *mut Z42Module) -> R,
) -> R {
    let user_data = capture as *const Mutex<Vec<u8>> as *mut c_void;
    let libs_path = libs_dir_cstring();
    let search_paths: [*const c_char; 2] = [libs_path.as_ptr(), ptr::null()];
    let cfg = Z42HostConfig {
        abi_version: Z42_HOST_ABI_VERSION,
        reserved: 0,
        exec_mode: config::Z42ExecMode::Interp as i32,
        heap_initial_bytes: 0,
        heap_max_bytes: 0,
        stdout_sink: Some(host_simple_capture_sink),
        stderr_sink: None,
        sink_user_data: user_data,
        search_paths: search_paths.as_ptr(),
    };
    let mut host: *mut Z42Host = ptr::null_mut();
    assert_eq!(
        unsafe { z42_host_initialize(&cfg, &mut host) },
        Z42HostStatus::Ok
    );
    let zbc_bytes: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/embedding_hello.zbc"));
    let mut module: *mut Z42Module = ptr::null_mut();
    let load_status = unsafe {
        z42_host_load_zbc(host, zbc_bytes.as_ptr(), zbc_bytes.len(), &mut module)
    };
    assert_eq!(load_status, Z42HostStatus::Ok);
    let result = body(host, module);
    assert_eq!(unsafe { z42_host_shutdown(host) }, Z42HostStatus::Ok);
    result
}

/// Capture-only sink used by H3 tests that don't care about extra
/// formatting — the StdoutCapture in `load_invoke_hello_world` adds an
/// `[host]` prefix; here we keep raw bytes for ordering assertions.
unsafe extern "C" fn host_simple_capture_sink(
    bytes: *const c_char,
    length: usize,
    user_data: *mut c_void,
) {
    if user_data.is_null() {
        return;
    }
    let mu = unsafe { &*(user_data as *const Mutex<Vec<u8>>) };
    let slice = if bytes.is_null() || length == 0 {
        &[][..]
    } else {
        unsafe { std::slice::from_raw_parts(bytes as *const u8, length) }
    };
    if let Ok(mut g) = mu.lock() {
        g.extend_from_slice(slice);
    }
}

#[test]
#[cfg(z42_have_embedding_hello)]
fn resolve_entry_unknown_fqn_returns_entry_not_found() {
    let _g = test_lock();
    reset_host();
    if !project_root().join("artifacts/z42/libs/z42.core.zpkg").is_file() {
        eprintln!("skipping: corelib zpkg not available");
        return;
    }

    let capture: Mutex<Vec<u8>> = Mutex::new(Vec::new());
    with_hello_session(&capture, |host, module| {
        let fqn = CString::new("Embedding.Hello.NoSuchMethod").unwrap();
        let mut entry: *mut Z42Entry = ptr::null_mut();
        let status =
            unsafe { z42_host_resolve_entry(host, module, fqn.as_ptr(), &mut entry) };
        assert_eq!(status, Z42HostStatus::EntryNotFound);
        assert!(entry.is_null());
        let err = unsafe { z42_host_last_error(ptr::null_mut()) };
        let msg = unsafe { CStr::from_ptr(err.message) }.to_string_lossy();
        assert!(
            msg.contains("NoSuchMethod"),
            "expected message to mention the missing FQN, got {msg}"
        );
    });
}

#[test]
#[cfg(z42_have_embedding_hello)]
fn invoke_arg_count_mismatch_returns_arg_mismatch() {
    let _g = test_lock();
    reset_host();
    if !project_root().join("artifacts/z42/libs/z42.core.zpkg").is_file() {
        eprintln!("skipping: corelib zpkg not available");
        return;
    }

    let capture: Mutex<Vec<u8>> = Mutex::new(Vec::new());
    with_hello_session(&capture, |host, module| {
        let fqn = CString::new("Embedding.Hello.Main").unwrap();
        let mut entry: *mut Z42Entry = ptr::null_mut();
        assert_eq!(
            unsafe { z42_host_resolve_entry(host, module, fqn.as_ptr(), &mut entry) },
            Z42HostStatus::Ok
        );

        // Main() takes zero args; passing one i64 must fail with ArgMismatch.
        let bogus_args = [z42_abi::Z42Value {
            tag: z42_abi::Z42_VALUE_TAG_I64,
            reserved: 0,
            payload: 42,
        }];
        let mut result = z42_abi::Z42Value {
            tag: u32::MAX,
            reserved: 0,
            payload: 0,
        };
        let status = unsafe {
            z42_host_invoke(entry, bogus_args.as_ptr(), bogus_args.len(), &mut result)
        };
        assert_eq!(status, Z42HostStatus::ArgMismatch);
        let err = unsafe { z42_host_last_error(ptr::null_mut()) };
        let msg = unsafe { CStr::from_ptr(err.message) }.to_string_lossy();
        assert!(msg.contains("expects 0"), "expected expects-0 detail, got {msg}");
        assert!(msg.contains("got 1"), "expected got-1 detail, got {msg}");
    });
}

#[test]
#[cfg(z42_have_embedding_hello)]
fn z42_throw_escapes_as_vm_exception_with_message() {
    let _g = test_lock();
    reset_host();
    if !project_root().join("artifacts/z42/libs/z42.core.zpkg").is_file() {
        eprintln!("skipping: corelib zpkg not available");
        return;
    }

    let capture: Mutex<Vec<u8>> = Mutex::new(Vec::new());
    with_hello_session(&capture, |host, module| {
        let fqn = CString::new("Embedding.Hello.Boom").unwrap();
        let mut entry: *mut Z42Entry = ptr::null_mut();
        assert_eq!(
            unsafe { z42_host_resolve_entry(host, module, fqn.as_ptr(), &mut entry) },
            Z42HostStatus::Ok
        );

        let mut result = z42_abi::Z42Value {
            tag: u32::MAX,
            reserved: 0,
            payload: 0,
        };
        let status = unsafe { z42_host_invoke(entry, ptr::null(), 0, &mut result) };
        assert_eq!(
            status,
            Z42HostStatus::VmException,
            "z42 throw must surface as VmException"
        );
        let err = unsafe { z42_host_last_error(ptr::null_mut()) };
        let msg = unsafe { CStr::from_ptr(err.message) }.to_string_lossy();
        assert!(
            msg.contains("intentional embedding-test failure"),
            "expected exception message to be propagated, got {msg}"
        );
    });
}

#[test]
#[cfg(z42_have_embedding_hello)]
fn sink_called_in_correct_order_for_multiple_lines() {
    let _g = test_lock();
    reset_host();
    if !project_root().join("artifacts/z42/libs/z42.core.zpkg").is_file() {
        eprintln!("skipping: corelib zpkg not available");
        return;
    }

    let capture: Mutex<Vec<u8>> = Mutex::new(Vec::new());
    with_hello_session(&capture, |host, module| {
        let fqn = CString::new("Embedding.Hello.MultiLine").unwrap();
        let mut entry: *mut Z42Entry = ptr::null_mut();
        assert_eq!(
            unsafe { z42_host_resolve_entry(host, module, fqn.as_ptr(), &mut entry) },
            Z42HostStatus::Ok
        );
        assert_eq!(
            unsafe { z42_host_invoke(entry, ptr::null(), 0, ptr::null_mut()) },
            Z42HostStatus::Ok
        );
    });

    let captured = String::from_utf8_lossy(&capture.lock().unwrap()).into_owned();
    assert_eq!(
        captured, "first\nsecond\nthird\n",
        "host stdout sink must receive lines in invocation order"
    );
}

#[test]
fn load_zbc_with_garbage_bytes_returns_bad_zbc() {
    let _g = test_lock();
    reset_host();

    let cfg = default_config();
    let mut h: *mut Z42Host = ptr::null_mut();
    assert_eq!(
        unsafe { z42_host_initialize(&cfg, &mut h) },
        Z42HostStatus::Ok
    );

    // Empty buffer — too short to even contain magic bytes.
    let mut out: *mut Z42Module = ptr::null_mut();
    let status = unsafe { z42_host_load_zbc(h, ptr::null(), 0, &mut out) };
    assert_eq!(status, Z42HostStatus::BadZbc);
    assert!(out.is_null());

    // Wrong-magic buffer — recognisable size but bytes are nonsense.
    let garbage = b"NOTAVALIDZBC".to_vec();
    let mut out2: *mut Z42Module = ptr::null_mut();
    let status =
        unsafe { z42_host_load_zbc(h, garbage.as_ptr(), garbage.len(), &mut out2) };
    assert_eq!(status, Z42HostStatus::BadZbc);
    assert!(out2.is_null());

    let err = unsafe { z42_host_last_error(ptr::null_mut()) };
    let msg = unsafe { CStr::from_ptr(err.message) }.to_string_lossy();
    assert!(
        msg.contains("magic") || msg.contains("zbc"),
        "expected magic/zbc detail, got {msg}"
    );

    assert_eq!(
        unsafe { z42_host_shutdown(h) },
        Z42HostStatus::Ok
    );
}
