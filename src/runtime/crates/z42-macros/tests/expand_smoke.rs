//! trybuild-driven smoke tests for the z42-macros expansion paths.
//!
//! Compile-fail cases verify diagnostics span the right tokens and the
//! message points at the spec that owns the fix. Pass cases verify the
//! emitted code at least type-checks (full integration tests live in
//! the z42_vm test crate where the runtime symbols are available).

#[test]
fn methods_macro_diagnostics() {
    let t = trybuild::TestCases::new();

    // Compile-fail diagnostics — pinned to expected stderr files.
    t.compile_fail("tests/fail/methods_missing_module.rs");
    t.compile_fail("tests/fail/methods_missing_name.rs");
    t.compile_fail("tests/fail/module_invalid_name.rs");
    t.compile_fail("tests/fail/derive_z42type_redirect.rs");
}
