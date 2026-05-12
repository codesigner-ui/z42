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
use std::path::{Path, PathBuf};
use std::process::Command;

fn main() {
    // Allow rustc to recognise our custom cfg without warning.
    println!("cargo:rustc-check-cfg=cfg(z42_skip_native_poc)");
    println!("cargo:rustc-check-cfg=cfg(z42_have_z42c)");
    println!("cargo:rustc-check-cfg=cfg(z42_have_embedding_hello)");
    println!("cargo:rerun-if-env-changed=Z42_SKIP_NATIVE_POC");
    // Auto-skip the C PoC when the build environment can't compile it:
    //   - explicit opt-out (Z42_SKIP_NATIVE_POC=1)
    //   - native-interop feature disabled (numz42-c is only ever consumed
    //     by tests/native_interop_e2e.rs, which is itself gated)
    //   - wasm32 target (no libc / stdlib.h in the wasm sandbox)
    //
    // 2026-05-12 add-platform-wasm Stage 0: the last two are auto-detection
    // so contributors building for wasm don't have to remember the env var.
    let native_interop_off = env::var("CARGO_FEATURE_NATIVE_INTEROP").is_err();
    let wasm_target = env::var("CARGO_CFG_TARGET_ARCH")
        .map(|a| a == "wasm32")
        .unwrap_or(false);
    if env::var_os("Z42_SKIP_NATIVE_POC").is_some() || native_interop_off || wasm_target {
        let reason = if env::var_os("Z42_SKIP_NATIVE_POC").is_some() {
            "Z42_SKIP_NATIVE_POC set"
        } else if wasm_target {
            "target is wasm32"
        } else {
            "native-interop feature is disabled"
        };
        println!("cargo:warning=skipping numz42-c build ({reason})");
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

    // ── Spec C7: compile the z42 e2e fixture if z42c is built ──────────
    //
    // The Rust integration test `z42_source_calls_numz42_via_native_attr`
    // needs a `.zbc` produced from `tests/data/z42_native_e2e/source.z42`.
    // Compile it here when the .NET driver is available; otherwise emit a
    // cargo:warning and rely on the test's own missing-fixture diagnostic.
    let project_root = manifest_dir.parent().and_then(Path::parent);
    let driver = project_root
        .map(|root| root.join("artifacts/build/compiler/z42.Driver/bin/z42c.dll"));
    if let Some(d) = &driver {
        println!("cargo:rerun-if-changed={}", d.display());
    }

    let driver_present = driver.as_ref().is_some_and(|d| d.is_file());

    let z42_src = manifest_dir.join("tests/data/z42_native_e2e/source.z42");
    if z42_src.is_file() {
        println!("cargo:rerun-if-changed={}", z42_src.display());
        if driver_present {
            let zbc_out = PathBuf::from(&out_dir).join("z42_native_e2e_source.zbc");
            let status = Command::new("dotnet")
                .arg(driver.as_ref().unwrap())
                .arg(&z42_src)
                .arg("--emit").arg("zbc")
                .arg("-o").arg(&zbc_out)
                .status();
            match status {
                Ok(s) if s.success() => {
                    println!("cargo:rustc-cfg=z42_have_z42c");
                }
                Ok(s) => {
                    println!("cargo:warning=z42c failed compiling C7 fixture (exit {s}); e2e test will be skipped");
                }
                Err(e) => {
                    println!("cargo:warning=could not invoke dotnet for C7 fixture: {e}; e2e test will be skipped");
                }
            }
        } else {
            println!("cargo:warning=z42c not built; run `dotnet build src/compiler/z42.slnx` then retry tests");
        }
    }

    // ── Embedding API H2: compile the hello-world fixture if z42c is built.
    //
    // The Rust integration test `host::host_tests::load_invoke_hello_world`
    // (gated behind `cfg(z42_have_embedding_hello)`) consumes a .zbc emitted
    // here. Same skip semantics as the C7 fixture — missing driver / failed
    // compile keeps the test out of the run instead of failing the build.
    let embedding_src = manifest_dir.join("tests/data/embedding_hello/source.z42");
    if embedding_src.is_file() {
        println!("cargo:rerun-if-changed={}", embedding_src.display());
        if driver_present {
            let zbc_out = PathBuf::from(&out_dir).join("embedding_hello.zbc");
            let status = Command::new("dotnet")
                .arg(driver.as_ref().unwrap())
                .arg(&embedding_src)
                .arg("--emit").arg("zbc")
                .arg("-o").arg(&zbc_out)
                .status();
            match status {
                Ok(s) if s.success() => {
                    println!("cargo:rustc-cfg=z42_have_embedding_hello");
                }
                Ok(s) => {
                    println!("cargo:warning=z42c failed compiling embedding-hello fixture (exit {s}); host integration test will be skipped");
                }
                Err(e) => {
                    println!("cargo:warning=could not invoke dotnet for embedding-hello fixture: {e}; host integration test will be skipped");
                }
            }
        }
    }
}
