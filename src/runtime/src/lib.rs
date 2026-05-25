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
// 2026-05-12 add-platform-wasm Stage 0: native interop (Tier 1 ABI for native
// code registering types into z42) requires libffi + dlopen which the wasm
// sandbox cannot provide. Gated on the `native-interop` feature.
#[cfg(feature = "native-interop")]
pub mod native;
// Embedding API (Tier 1 C ABI for host applications). Spec:
// docs/design/runtime/embedding.md, docs/spec/archive/2026-05-10-add-embedding-api/.
pub mod host;
pub mod vm;
pub mod vm_context;

// Runtime configuration registry — single source of truth for every
// Z42_* env var the runtime reads (docs/review.md Part 4 D1, 2026-05-26).
// `RuntimeConfig` carries the 5 startup-consumed knobs; `KNOWN_KNOBS`
// table also lists subsystem-local knobs (Z42_GC_* / Z42_NATIVE_PATH /
// Z42_SAFEPOINT_THROTTLE / Z42_STRESS_ITERS) for `--info` discovery.
pub mod config;

// POSIX signal handler — captures z42 call stack on hard crashes
// (SIGSEGV / SIGABRT / SIGFPE / SIGILL / SIGBUS). Phase 2 of D4 panic-hook
// story (Phase 1 = main.rs install_panic_hook). Windows VEH = Phase 2.1.
// Spec: docs/spec/changes/add-os-signal-handler/.
#[cfg(unix)]
pub mod signal_handler;
