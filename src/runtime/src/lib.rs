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
