pub mod metadata;
pub mod corelib;
pub mod gc;
pub mod thread;
pub mod exception;
pub mod interp;
// 2026-05-07 add-runtime-feature-flags (P4.1): jit / aot are feature-gated.
// `default = ["jit"]` keeps backward compat; platforms (wasm / ios / android)
// build without the JIT backend by activating their preset features.
#[cfg(feature = "jit")]
pub mod jit;
#[cfg(feature = "aot")]
pub mod aot;
pub mod native;
pub mod vm;
pub mod vm_context;
