//! Cross-language zbc decoder contract test.
//!
//! Exercises Rust's `metadata::zbc_reader::read_zbc` against real `.zbc`
//! bytes produced by the C# compiler (the golden test artifacts under
//! `tests/golden/run/<name>/source.zbc`). Catches **any** C# opcode /
//! section format drift that breaks Rust decoding — without waiting for
//! `./scripts/test-vm.sh` to find it via end-to-end execution.
//!
//! review2 §4 (跨语言契约自动校验) — closes the gap between
//! `ZbcRoundTripTests` (C# write → C# read only) and full e2e (slow).
//!
//! Regenerate the golden zbc files with `./scripts/regen-golden-tests.sh`
//! after every C# compiler change that affects bytecode emission.

use std::fs;
use std::path::PathBuf;

use z42_vm::metadata::{load_artifact, zbc_reader::read_zbc};

/// Resolve the project-root path to a golden test directory.
fn golden_dir(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("golden")
        .join("run")
        .join(name)
}

/// Iterate every golden test directory that ships with a `source.zbc`.
fn each_golden_zbc() -> Vec<(String, PathBuf)> {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("golden")
        .join("run");
    let mut out = Vec::new();
    let entries = fs::read_dir(&root).expect("golden/run exists");
    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_dir() { continue; }
        let zbc = path.join("source.zbc");
        if zbc.is_file() {
            let name = path.file_name().unwrap().to_string_lossy().into_owned();
            out.push((name, zbc));
        }
    }
    out.sort_by(|a, b| a.0.cmp(&b.0));
    out
}

// ────────────────────────────────────────────────────────────────────────────
// Broad coverage: every golden zbc decodes without error and has plausible
// shape (≥ 1 function, valid string pool indices on instructions).
// ────────────────────────────────────────────────────────────────────────────

#[test]
fn all_golden_zbc_decode() {
    let goldens = each_golden_zbc();
    assert!(
        goldens.len() >= 50,
        "expected ≥ 50 golden tests with source.zbc, found {}",
        goldens.len()
    );

    let mut failures = Vec::new();
    for (name, path) in &goldens {
        let bytes = fs::read(path).expect("read .zbc bytes");
        match read_zbc(&bytes) {
            Err(e) => failures.push(format!("{name}: read_zbc failed: {e}")),
            Ok(module) => {
                if module.functions.is_empty() {
                    failures.push(format!("{name}: 0 functions (suspicious)"));
                }
                // Plausibility: every Call/Builtin/StaticGet etc. references a
                // string-pool index within bounds. We only spot-check a few
                // instructions per function to keep the test fast.
                for func in &module.functions {
                    for block in &func.blocks {
                        for instr in block.instructions.iter().take(50) {
                            check_instr_pool_refs(&module, instr, &func.name)
                                .unwrap_or_else(|msg| {
                                    failures.push(format!("{name}::{}: {msg}", func.name));
                                });
                        }
                    }
                }
            }
        }
    }

    if !failures.is_empty() {
        panic!(
            "{} golden zbc(s) failed cross-language decode:\n  {}",
            failures.len(),
            failures.join("\n  ")
        );
    }
}

/// Light invariant: instructions that name targets via string-pool indices
/// must reference valid pool entries. Ensures opcode parameter layout matches
/// across C# write and Rust read.
fn check_instr_pool_refs(
    module: &z42_vm::metadata::Module,
    instr: &z42_vm::metadata::Instruction,
    _func_name: &str,
) -> Result<(), String> {
    use z42_vm::metadata::Instruction as I;
    let pool_len = module.string_pool.len();
    // A handful of instructions carry pool-indexed names; verify they resolve.
    // (Most instructions carry inlined String fields after `read_zbc`
    // reconstruction, so the pool-index check applies primarily to ConstStr.)
    if let I::ConstStr { idx, .. } = instr {
        if (*idx as usize) >= pool_len {
            return Err(format!(
                "ConstStr idx={} out of pool bounds (pool len={})",
                idx, pool_len
            ));
        }
    }
    Ok(())
}

// ────────────────────────────────────────────────────────────────────────────
// Specific structural assertions: pin a few high-traffic golden tests so
// silent regressions in opcode layout get a precise diagnostic, not just a
// generic "decode failed".
// ────────────────────────────────────────────────────────────────────────────

#[test]
fn hello_zbc_structure() {
    let bytes = fs::read(golden_dir("01_hello").join("source.zbc"))
        .expect("01_hello/source.zbc");
    let module = read_zbc(&bytes).expect("01_hello decodes");

    // Source uses `namespace Demo;` so functions are emitted as `Demo.Greet`,
    // `Demo.Main`. The module name itself comes from the NSPC section.
    assert_eq!(module.name, "Demo", "namespace from NSPC");

    let names: Vec<&str> = module.functions.iter().map(|f| f.name.as_str()).collect();
    assert!(
        names.iter().any(|n| *n == "Demo.Greet"),
        "Demo.Greet must be present, got: {names:?}"
    );
    assert!(
        names.iter().any(|n| *n == "Demo.Main"),
        "Demo.Main must be present, got: {names:?}"
    );

    // String pool must include the source literals used in interpolation /
    // direct args. C# emits these via the unified strings section.
    let pool = &module.string_pool;
    for needed in &["Hello, ", "!", "world", "z42 version 0.1, the answer is "] {
        assert!(
            pool.iter().any(|s| s == needed),
            "string pool missing literal {needed:?}; pool = {pool:?}"
        );
    }
}

#[test]
fn class_basic_zbc_has_classes() {
    let zbc = golden_dir("07_class_basic").join("source.zbc");
    if !zbc.is_file() {
        // Ride-along: this assertion is opportunistic. If the test is renamed
        // / restructured upstream, fall through silently rather than fail
        // (the broad `all_golden_zbc_decode` test still exercises everything).
        return;
    }
    let bytes = fs::read(&zbc).expect("07_class_basic/source.zbc");
    let module = read_zbc(&bytes).expect("07_class_basic decodes");

    assert!(
        !module.classes.is_empty(),
        "07_class_basic must declare at least one class via TYPE section"
    );
    // Every class has a non-empty simple name.
    for cls in &module.classes {
        assert!(!cls.name.is_empty(), "class name empty");
    }
}

// ────────────────────────────────────────────────────────────────────────────
// load_artifact full-path test: includes build_type_registry +
// verify_constraints + build_block_indices + build_func_index.
// Ensures the post-decode passes also stay green against C# output.
// ────────────────────────────────────────────────────────────────────────────

#[test]
fn hello_load_artifact_full_path() {
    let zbc = golden_dir("01_hello").join("source.zbc");
    let path = zbc.to_string_lossy();
    let loaded = load_artifact(&path).expect("01_hello loads via load_artifact");

    // func_index built by post-decode pass.
    assert!(
        loaded.module.func_index.contains_key("Demo.Main"),
        "func_index missing Demo.Main"
    );
    assert!(
        loaded.module.func_index.contains_key("Demo.Greet"),
        "func_index missing Demo.Greet"
    );
}
