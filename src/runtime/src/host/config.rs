//! Host configuration: C ABI struct layout + validation.
//!
//! Mirrors `Z42HostConfig` / `Z42WriteSink` / `Z42ExecMode` from
//! `src/runtime/include/z42_host.h`. Spec: docs/design/embedding.md §4.2.

use std::os::raw::{c_char, c_void};

/// Wire-compatible version of `Z42_HOST_ABI_VERSION` from the C header.
pub const Z42_HOST_ABI_VERSION: u32 = 1;

/// `Z42WriteSink` callback. `Option` so a null function pointer maps to
/// "use the configured default" (real stdout / accumulating sink / etc.).
pub type Z42WriteSink =
    Option<unsafe extern "C" fn(bytes: *const c_char, length: usize, user_data: *mut c_void)>;

/// `Z42ExecMode` — must stay in sync with the C enum.
#[repr(i32)]
#[derive(Debug, Copy, Clone, PartialEq, Eq)]
pub enum Z42ExecMode {
    Default = 0,
    Interp = 1,
    Jit = 2,
    Aot = 3,
}

impl Z42ExecMode {
    /// Convert from a raw C enum value (`i32`). Unknown values → `None`.
    pub fn from_raw(raw: i32) -> Option<Self> {
        match raw {
            0 => Some(Self::Default),
            1 => Some(Self::Interp),
            2 => Some(Self::Jit),
            3 => Some(Self::Aot),
            _ => None,
        }
    }
}

/// C ABI layout of `Z42HostConfig`. `#[repr(C)]` keeps field order +
/// alignment compatible with the C struct in `z42_host.h`.
#[repr(C)]
pub struct Z42HostConfig {
    pub abi_version: u32,
    pub reserved: u32,

    pub exec_mode: i32, // Z42ExecMode wire form (avoid Rust enum UB on bad input)
    pub heap_initial_bytes: usize,
    pub heap_max_bytes: usize,

    pub stdout_sink: Z42WriteSink,
    pub stderr_sink: Z42WriteSink,
    pub sink_user_data: *mut c_void,

    pub search_paths: *const *const c_char,
}

/// Rust-side resolved configuration after validation. The thread-safety
/// requirement comes from holding this inside the global `RwLock` —
/// `*mut c_void` is not auto-`Send`, so we wrap the whole config in
/// `unsafe impl Send + Sync` at the storage site (see `state.rs`).
#[derive(Debug)]
pub struct ResolvedConfig {
    pub exec_mode: Z42ExecMode,
    pub heap_initial_bytes: usize,
    pub heap_max_bytes: usize,
    pub stdout_sink: Z42WriteSink,
    pub stderr_sink: Z42WriteSink,
    pub sink_user_data: usize, // raw pointer kept as usize so this struct is Send
    pub search_paths: Vec<String>,
}

/// Validation outcome. Caller maps `Err` to a `Z42HostStatus`.
#[derive(Debug)]
pub enum ConfigError {
    NullConfig,
    AbiVersionMismatch { expected: u32, got: u32 },
    UnknownExecMode { raw: i32 },
    FeatureOff { mode: Z42ExecMode },
    BadSearchPath { reason: &'static str },
}

/// Validate a raw `*const Z42HostConfig` from the FFI boundary.
///
/// # Safety
/// Caller must ensure that, if `cfg` is non-null, it points to a valid
/// `Z42HostConfig` with a valid `search_paths` array (NULL-terminated, or
/// null pointer if no paths are configured).
pub(crate) unsafe fn validate(cfg: *const Z42HostConfig) -> Result<ResolvedConfig, ConfigError> {
    if cfg.is_null() {
        return Err(ConfigError::NullConfig);
    }
    let cfg = unsafe { &*cfg };

    if cfg.abi_version != Z42_HOST_ABI_VERSION {
        return Err(ConfigError::AbiVersionMismatch {
            expected: Z42_HOST_ABI_VERSION,
            got: cfg.abi_version,
        });
    }

    let exec_mode = Z42ExecMode::from_raw(cfg.exec_mode)
        .ok_or(ConfigError::UnknownExecMode { raw: cfg.exec_mode })?;

    check_feature_available(exec_mode)?;

    let search_paths = unsafe { collect_search_paths(cfg.search_paths) }?;

    Ok(ResolvedConfig {
        exec_mode,
        heap_initial_bytes: cfg.heap_initial_bytes,
        heap_max_bytes: cfg.heap_max_bytes,
        stdout_sink: cfg.stdout_sink,
        stderr_sink: cfg.stderr_sink,
        sink_user_data: cfg.sink_user_data as usize,
        search_paths,
    })
}

fn check_feature_available(mode: Z42ExecMode) -> Result<(), ConfigError> {
    match mode {
        Z42ExecMode::Default | Z42ExecMode::Interp => Ok(()),
        Z42ExecMode::Jit => {
            #[cfg(feature = "jit")]
            {
                Ok(())
            }
            #[cfg(not(feature = "jit"))]
            {
                Err(ConfigError::FeatureOff { mode })
            }
        }
        Z42ExecMode::Aot => {
            #[cfg(feature = "aot")]
            {
                Ok(())
            }
            #[cfg(not(feature = "aot"))]
            {
                Err(ConfigError::FeatureOff { mode })
            }
        }
    }
}

unsafe fn collect_search_paths(
    raw: *const *const c_char,
) -> Result<Vec<String>, ConfigError> {
    if raw.is_null() {
        return Ok(Vec::new());
    }
    let mut out = Vec::new();
    let mut cursor = raw;
    // Bound the loop so a malformed (un-NUL-terminated) array can't iterate
    // forever. 4096 entries is far more than any real host would supply.
    for _ in 0..4096 {
        let entry = unsafe { *cursor };
        if entry.is_null() {
            return Ok(out);
        }
        let cstr = unsafe { std::ffi::CStr::from_ptr(entry) };
        let s = cstr
            .to_str()
            .map_err(|_| ConfigError::BadSearchPath {
                reason: "search path is not valid UTF-8",
            })?;
        out.push(s.to_owned());
        cursor = unsafe { cursor.add(1) };
    }
    Err(ConfigError::BadSearchPath {
        reason: "search_paths array exceeded 4096 entries (missing NULL terminator?)",
    })
}

