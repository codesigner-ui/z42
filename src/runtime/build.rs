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

    // ── Spec C7: compile the z42 e2e fixture if z42c is built ──────────
    //
    // The Rust integration test `z42_source_calls_numz42_via_native_attr`
    // needs a `.zbc` produced from `tests/data/z42_native_e2e/source.z42`.
    // Compile it here when the .NET driver is available; otherwise emit a
    // cargo:warning and rely on the test's own missing-fixture diagnostic.
    let z42_src = manifest_dir.join("tests/data/z42_native_e2e/source.z42");
    if z42_src.is_file() {
        println!("cargo:rerun-if-changed={}", z42_src.display());
        let project_root = manifest_dir.parent().and_then(Path::parent);
        if let Some(root) = project_root {
            let driver = root.join("artifacts/compiler/z42.Driver/bin/z42c.dll");
            println!("cargo:rerun-if-changed={}", driver.display());
            if driver.is_file() {
                // Emit into OUT_DIR (under target/), not the source tree —
                // keeps `git status` clean across test runs.
                let zbc_out = PathBuf::from(&out_dir).join("z42_native_e2e_source.zbc");
                let status = Command::new("dotnet")
                    .arg(&driver)
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
    }
}
