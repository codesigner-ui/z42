//! Build script: compiles the `numz42-c` PoC native library used by spec
//! C2's end-to-end integration test.
//!
//! The library is built as a static archive and linked into any cargo test
//! binary that consumes z42_vm. Tests call `numz42_register_static()`
//! directly, so the dlopen path is exercised separately when present.
//!
//! Set `Z42_SKIP_NATIVE_POC=1` to opt out (e.g. on CI agents without a C
//! toolchain); `tests/native_interop_e2e.rs` will then skip its scenarios.

use std::env;
use std::path::PathBuf;

fn main() {
    // Allow rustc to recognise our custom cfg without warning.
    println!("cargo:rustc-check-cfg=cfg(z42_skip_native_poc)");
    println!("cargo:rerun-if-env-changed=Z42_SKIP_NATIVE_POC");
    if env::var_os("Z42_SKIP_NATIVE_POC").is_some() {
        println!("cargo:warning=Z42_SKIP_NATIVE_POC set — skipping numz42-c build");
        println!("cargo:rustc-cfg=z42_skip_native_poc");
        return;
    }

    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let src   = manifest_dir.join("tests/data/numz42-c/numz42.c");
    let inc   = manifest_dir.join("include");

    println!("cargo:rerun-if-changed={}", src.display());
    println!("cargo:rerun-if-changed={}/z42_abi.h", inc.display());

    cc::Build::new()
        .file(&src)
        .include(&inc)
        .flag_if_supported("-Wall")
        .flag_if_supported("-Wextra")
        .flag_if_supported("-Wno-unused-function")
        .compile("numz42_c"); // emits libnumz42_c.a in OUT_DIR

    // Tests that link against this archive opt-in via #[link] in source —
    // we only set the static-lib search path here.
    let out_dir = env::var("OUT_DIR").unwrap();
    println!("cargo:rustc-link-search=native={out_dir}");
}
