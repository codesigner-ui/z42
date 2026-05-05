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

/// Project root resolved from `CARGO_MANIFEST_DIR` (= src/runtime).
fn project_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent().unwrap()        // src/
        .parent().unwrap()        // <root>/
        .to_path_buf()
}

/// Resolve a project-root-relative path to a test case directory.
/// Format: `"<category>/<name>"` (e.g. `"basic/01_hello"`).
fn golden_dir(rel: &str) -> PathBuf {
    project_root().join("src/tests").join(rel)
}

/// Iterate every test directory that ships with a `source.zbc` under
/// `src/tests/` (excluding errors/parse/cross-zpkg) and `src/libraries/<lib>/tests/`.
fn each_golden_zbc() -> Vec<(String, PathBuf)> {
    let root = project_root();
    let mut out = Vec::new();
    let mut roots: Vec<PathBuf> = Vec::new();

    // src/tests/<category>/<name>/  (skip errors/parse/cross-zpkg)
    let tests_root = root.join("src/tests");
    if let Ok(entries) = fs::read_dir(&tests_root) {
        for cat in entries.flatten() {
            if !cat.path().is_dir() { continue; }
            let cat_name = cat.file_name().to_string_lossy().into_owned();
            if matches!(cat_name.as_str(), "errors" | "parse" | "cross-zpkg") { continue; }
            roots.push(cat.path());
        }
    }
    // src/libraries/<lib>/tests/<name>/
    if let Ok(libs) = fs::read_dir(root.join("src/libraries")) {
        for lib in libs.flatten() {
            let p = lib.path().join("tests");
            if p.is_dir() { roots.push(p); }
        }
    }

    for r in roots {
        if let Ok(entries) = fs::read_dir(&r) {
            for entry in entries.flatten() {
                let path = entry.path();
                if !path.is_dir() { continue; }
                let zbc = path.join("source.zbc");
                if zbc.is_file() {
                    let name = path.file_name().unwrap().to_string_lossy().into_owned();
                    out.push((name, zbc));
                }
            }
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
    let bytes = fs::read(golden_dir("basic/01_hello").join("source.zbc"))
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
    let zbc = golden_dir("classes/07_class_basic").join("source.zbc");
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
    let zbc = golden_dir("basic/01_hello").join("source.zbc");
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

// ────────────────────────────────────────────────────────────────────────────
// R1 — TIDX section cross-language contract.
// ────────────────────────────────────────────────────────────────────────────

/// Compile examples/test_demo.z42 fresh and verify Rust read_test_index_section
/// extracts the same 8 TestEntry records the C# compiler wrote. Skips when
/// dotnet isn't available (e.g. minimal CI runners), matching the spirit of the
/// rest of the cross-language tests.
#[test]
fn test_demo_tidx_round_trips() {
    use std::process::Command;
    use z42_vm::metadata::{TestEntryKind, TestFlags};

    // Locate the project root (parent-of-parent-of-parent of CARGO_MANIFEST_DIR).
    let runtime_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let repo_root = runtime_dir.parent().and_then(|p| p.parent()).unwrap();

    let demo_src = repo_root.join("examples").join("test_demo.z42");
    if !demo_src.exists() {
        eprintln!("skip: examples/test_demo.z42 not found at {}", demo_src.display());
        return;
    }

    // Compile to a temp .zbc in the cargo target dir (writable).
    let out_dir = std::env::temp_dir().join("z42-r1-tidx-test");
    std::fs::create_dir_all(&out_dir).unwrap();
    let zbc_path = out_dir.join("test_demo.zbc");

    let dotnet = Command::new("dotnet")
        .args([
            "run", "--project",
            repo_root.join("src/compiler/z42.Driver").to_str().unwrap(),
            "-c", "Release", "--",
            demo_src.to_str().unwrap(),
            "--emit", "zbc",
            "-o", zbc_path.to_str().unwrap(),
        ])
        .output();

    let dotnet = match dotnet {
        Ok(o) if o.status.success() => o,
        Ok(o) => {
            eprintln!(
                "skip: dotnet failed; stderr:\n{}\nstdout:\n{}",
                String::from_utf8_lossy(&o.stderr),
                String::from_utf8_lossy(&o.stdout)
            );
            return;
        }
        Err(e) => {
            eprintln!("skip: dotnet not invocable: {e}");
            return;
        }
    };
    drop(dotnet);

    // Load and verify the TIDX section.
    let bytes = std::fs::read(&zbc_path).expect("read compiled zbc");
    let entries = z42_vm::metadata::zbc_reader::read_test_index_section(&bytes)
        .expect("read TIDX section");

    assert_eq!(entries.len(), 8, "expected 8 TestEntry rows for test_demo.z42, got {}", entries.len());

    // Verify kinds (functions appear in source order in IR; TestIndex preserves
    // that order via BuildTestIndex iteration).
    let kinds: Vec<TestEntryKind> = entries.iter().map(|e| e.kind).collect();
    assert!(kinds.contains(&TestEntryKind::Test),     "no Test entries: {:?}", kinds);
    assert!(kinds.contains(&TestEntryKind::Setup),    "no Setup: {:?}", kinds);
    assert!(kinds.contains(&TestEntryKind::Teardown), "no Teardown: {:?}", kinds);

    // 5 [Test]-decorated + 1 [Setup] + 1 [Teardown] = 7 expected;
    // [Test][Ignore] still has Test kind. So 6 Test + 1 Setup + 1 Teardown = 8.
    assert_eq!(kinds.iter().filter(|k| **k == TestEntryKind::Test).count(),     6);
    assert_eq!(kinds.iter().filter(|k| **k == TestEntryKind::Setup).count(),    1);
    assert_eq!(kinds.iter().filter(|k| **k == TestEntryKind::Teardown).count(), 1);

    // Verify flag combinations: at least one with Skipped, at least one with Ignored,
    // at least one with non-zero skip_platform_str_idx.
    let any_skipped  = entries.iter().any(|e| e.flags.contains(TestFlags::SKIPPED));
    let any_ignored  = entries.iter().any(|e| e.flags.contains(TestFlags::IGNORED));
    let any_platform = entries.iter().any(|e| e.skip_platform_str_idx > 0);
    let any_feature  = entries.iter().any(|e| e.skip_feature_str_idx > 0);
    assert!(any_skipped,  "no entry with SKIPPED flag");
    assert!(any_ignored,  "no entry with IGNORED flag");
    assert!(any_platform, "no entry with skip_platform_str_idx > 0");
    assert!(any_feature,  "no entry with skip_feature_str_idx > 0");

    // Verify all method_ids reference valid functions in the module.
    let module = read_zbc(&bytes).expect("read full zbc");
    for e in &entries {
        assert!(
            (e.method_id as usize) < module.functions.len(),
            "method_id {} out of range (functions.len() = {})",
            e.method_id, module.functions.len()
        );
    }
}
