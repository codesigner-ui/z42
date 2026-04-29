//! End-to-end integration test for spec C2 (`impl-tier1-c-abi`).
//!
//! Links `numz42_c` (a C library compiled by build.rs from
//! `tests/data/numz42-c/numz42.c`) into this test binary and exercises the
//! full `register → CallNative → marshal → libffi → unmarshal` pipeline.
//!
//! No dlopen here — the static-link path validates the runtime without the
//! cross-binary symbol-export complications of `libloading`. Once the
//! source generator (C5) wires user-facing syntax we will add a separate
//! dlopen-based smoke test.

#![cfg(not(z42_skip_native_poc))]

use std::collections::HashMap;

use z42_vm::metadata::{
    BasicBlock, ExecMode, Function, Instruction, Module, Terminator, Value,
};
use z42_vm::vm_context::VmContext;

extern "C" {
    /// Linked statically from `libnumz42_c.a`. Calls `z42_register_type`
    /// for the Counter type. Must run with a `VmGuard` active so the
    /// `CURRENT_VM` thread-local has a valid pointer.
    fn numz42_register_static();
}

// ── Rust-side PoC (spec C3) ────────────────────────────────────────────────
//
// Mirrors `tests/data/numz42-c/numz42.c` but uses the C3 ergonomic macros
// from `z42-rs` instead of hand-writing a `Z42TypeDescriptor_v1` literal.

mod numz42_rs {
    use ::z42_rs::prelude::*;
    use ::z42_rs as z42;

    #[derive(Default)]
    pub struct Counter {
        pub value: i64,
    }

    #[z42::methods(module = "numz42_rs", name = "Counter")]
    impl Counter {
        pub fn inc(&mut self) -> i64 { self.value += 1; self.value }
        pub fn get(&self) -> i64 { self.value }
    }

    z42::module! {
        name: "numz42_rs",
        types: [Counter],
    }
}

extern "C" {
    /// Emitted by the `z42::module!` macro inside `mod numz42_rs`.
    fn numz42_rs_register();
}

fn build_function(name: &str, instructions: Vec<Instruction>, terminator: Terminator) -> Function {
    Function {
        name: name.to_string(),
        param_count: 0,
        ret_type: "i64".to_string(),
        exec_mode: ExecMode::Interp,
        blocks: vec![BasicBlock {
            label: "entry".to_string(),
            instructions,
            terminator,
        }],
        exception_table: vec![],
        is_static: true,
        max_reg: 4,
        line_table: vec![],
        local_vars: vec![],
        type_params: vec![],
        type_param_constraints: vec![],
        block_index: HashMap::new(),
    }
}

fn build_module(name: &str, instructions: Vec<Instruction>, terminator: Terminator) -> Module {
    Module {
        name: name.to_string(),
        string_pool: vec![],
        classes: vec![],
        functions: vec![build_function(&format!("{name}.Main"), instructions, terminator)],
        type_registry: HashMap::new(),
        func_index: HashMap::new(),
    }
}

/// Bring up a VmContext with `numz42::Counter` registered.
///
/// The register entry point requires `CURRENT_VM` to be set so the
/// `z42_register_type` extern function can find the active VM. We install
/// the same `VmGuard` the interpreter uses, call the C entry, then drop
/// the guard before handing the VM to the test body.
fn vm_with_counter_registered() -> VmContext {
    let ctx = VmContext::new();
    invoke_with_vm_guard(&ctx, || unsafe { numz42_register_static() });
    ctx
}

/// Run `f` while a `VmGuard` for `ctx` is active. The guard is normally
/// installed by `interp::exec_function`; this helper exposes the same
/// scoping for tests that need to drive `z42_*` extern functions directly.
fn invoke_with_vm_guard<R>(ctx: &VmContext, f: impl FnOnce() -> R) -> R {
    let _g = z42_vm::native::exports::VmGuard::enter(ctx);
    f()
}

#[test]
fn register_counter_then_resolve() {
    let ctx = vm_with_counter_registered();
    let ty = ctx
        .resolve_native_type("numz42", "Counter")
        .expect("Counter resolves after register");
    assert_eq!(ty.module(), "numz42");
    assert_eq!(ty.type_name(), "Counter");
    assert_eq!(ty.method_count(), 3);
    assert!(ty.method("inc").is_some());
    assert!(ty.method("get").is_some());
    assert!(ty.method("__alloc__").is_some());
}

#[test]
fn callnative_counter_inc_three_times_then_get_returns_three() {
    let ctx = vm_with_counter_registered();

    // Hand-crafted IR:
    //   r0 = call.native numz42::Counter::__alloc__()
    //   _  = call.native numz42::Counter::inc(r0)   ; ignore return
    //   _  = call.native numz42::Counter::inc(r0)
    //   _  = call.native numz42::Counter::inc(r0)
    //   r1 = call.native numz42::Counter::get(r0)
    //   ret r1
    let instructions = vec![
        Instruction::CallNative {
            dst: 0,
            module: "numz42".into(),
            type_name: "Counter".into(),
            symbol: "__alloc__".into(),
            args: vec![],
        },
        Instruction::CallNative {
            dst: 2,
            module: "numz42".into(),
            type_name: "Counter".into(),
            symbol: "inc".into(),
            args: vec![0],
        },
        Instruction::CallNative {
            dst: 2,
            module: "numz42".into(),
            type_name: "Counter".into(),
            symbol: "inc".into(),
            args: vec![0],
        },
        Instruction::CallNative {
            dst: 2,
            module: "numz42".into(),
            type_name: "Counter".into(),
            symbol: "inc".into(),
            args: vec![0],
        },
        Instruction::CallNative {
            dst: 1,
            module: "numz42".into(),
            type_name: "Counter".into(),
            symbol: "get".into(),
            args: vec![0],
        },
    ];
    let m = build_module("counter_e2e", instructions, Terminator::Ret { reg: Some(1) });
    let func = &m.functions[0];

    let result = z42_vm::interp::run_returning(&ctx, &m, func, &[] as &[Value])
        .expect("counter_e2e returns Ok");
    assert_eq!(
        result,
        Some(Value::I64(3)),
        "Counter::get should return 3 after 3 incs"
    );
}

// ── Rust PoC scenarios (spec C3) ─────────────────────────────────────────

fn vm_with_rust_counter_registered() -> VmContext {
    let ctx = VmContext::new();
    invoke_with_vm_guard(&ctx, || unsafe { numz42_rs_register() });
    ctx
}

#[test]
fn rust_counter_register_and_resolve() {
    let ctx = vm_with_rust_counter_registered();
    let ty = ctx
        .resolve_native_type("numz42_rs", "Counter")
        .expect("Rust Counter resolves after register");
    assert_eq!(ty.module(), "numz42_rs");
    assert_eq!(ty.type_name(), "Counter");
    assert!(ty.method("inc").is_some());
    assert!(ty.method("get").is_some());
}

#[test]
fn rust_counter_callnative_inc_three_times_then_get_returns_three() {
    let ctx = vm_with_rust_counter_registered();

    // Same IR shape as the C version, but the Rust descriptor doesn't
    // expose an `__alloc__` static method — instead the macro emits a
    // VM-internal alloc fn we don't surface as a method. Instead we let
    // the user-side test call alloc through the descriptor by invoking
    // the alloc function directly via a synthetic approach:
    //
    // Rust PoC strategy: drive the Counter through alloc helper that
    // we expose as a static method named `alloc` (added in PoC). Since
    // C3 macro doesn't yet support static methods alongside instance
    // methods elegantly, we call descriptor.alloc() from Rust here.
    let ty = ctx.resolve_native_type("numz42_rs", "Counter").unwrap();
    let alloc_fn = unsafe { (*ty.descriptor_ptr()).alloc.expect("alloc set") };
    let counter_ptr = unsafe { alloc_fn() };
    let ctor_fn = unsafe { (*ty.descriptor_ptr()).ctor.expect("ctor set") };
    unsafe { ctor_fn(counter_ptr, std::ptr::null()) };
    let counter_as_i64 = counter_ptr as i64;

    let instructions = vec![
        Instruction::ConstI64 { dst: 0, val: counter_as_i64 },
        Instruction::CallNative {
            dst: 2,
            module: "numz42_rs".into(),
            type_name: "Counter".into(),
            symbol: "inc".into(),
            args: vec![0],
        },
        Instruction::CallNative {
            dst: 2,
            module: "numz42_rs".into(),
            type_name: "Counter".into(),
            symbol: "inc".into(),
            args: vec![0],
        },
        Instruction::CallNative {
            dst: 2,
            module: "numz42_rs".into(),
            type_name: "Counter".into(),
            symbol: "inc".into(),
            args: vec![0],
        },
        Instruction::CallNative {
            dst: 1,
            module: "numz42_rs".into(),
            type_name: "Counter".into(),
            symbol: "get".into(),
            args: vec![0],
        },
    ];
    let m = build_module(
        "rust_counter_e2e",
        instructions,
        Terminator::Ret { reg: Some(1) },
    );
    let func = &m.functions[0];

    let result = z42_vm::interp::run_returning(&ctx, &m, func, &[] as &[Value])
        .expect("rust_counter_e2e returns Ok");
    assert_eq!(result, Some(Value::I64(3)));

    // Cleanup: dealloc the Counter via the descriptor.
    let dealloc_fn = unsafe { (*ty.descriptor_ptr()).dealloc.expect("dealloc set") };
    let dtor_fn = unsafe { (*ty.descriptor_ptr()).dtor.expect("dtor set") };
    unsafe { dtor_fn(counter_ptr) };
    unsafe { dealloc_fn(counter_ptr) };
}

#[test]
fn c_and_rust_modules_coexist() {
    let ctx = VmContext::new();
    invoke_with_vm_guard(&ctx, || unsafe { numz42_register_static() });
    invoke_with_vm_guard(&ctx, || unsafe { numz42_rs_register() });

    let c_ty = ctx
        .resolve_native_type("numz42", "Counter")
        .expect("C Counter present");
    let rs_ty = ctx
        .resolve_native_type("numz42_rs", "Counter")
        .expect("Rust Counter present");

    // Same simple name `Counter` distinguishes by module — no clash.
    assert_eq!(c_ty.module(), "numz42");
    assert_eq!(rs_ty.module(), "numz42_rs");
    assert_ne!(
        std::rc::Rc::as_ptr(&c_ty) as *const _,
        std::rc::Rc::as_ptr(&rs_ty) as *const _
    );
}

// ── Spec C7: end-to-end from real z42 source ──────────────────────────────

/// Loads `tests/data/z42_native_e2e/source.zbc` (compiled from a `.z42`
/// program that uses `[Native(lib=, type=, entry=)]` to call into the
/// statically-linked `numz42-c` Counter), pre-registers the type, runs
/// `Main`, and checks the returned value. Closes the C2→C6 loop:
///
///   z42 source  →  z42c  →  zbc  →  VM dispatch  →  numz42-c  →  i64
///
/// If the .zbc file is regenerated (e.g. compiler IR change), refresh
/// it via:
///
///   dotnet artifacts/compiler/z42.Driver/bin/Debug/net10.0/z42c.dll \
///       src/runtime/tests/data/z42_native_e2e/source.z42 --emit zbc \
///       -o src/runtime/tests/data/z42_native_e2e/source.zbc
#[test]
#[cfg(z42_have_z42c)]
fn z42_source_calls_numz42_via_native_attr() {
    use std::path::PathBuf;

    // build.rs compiles `tests/data/z42_native_e2e/source.z42` via z42c
    // when the .NET driver is built and writes the output to OUT_DIR. If
    // z42c isn't available, the `cfg(z42_have_z42c)` gate keeps this test
    // out of the run with a build-time `cargo:warning`.
    let zbc_path = PathBuf::from(env!("OUT_DIR")).join("z42_native_e2e_source.zbc");
    if !zbc_path.is_file() {
        panic!(
            "fixture missing in OUT_DIR ({}); rebuild z42c then `cargo test`",
            zbc_path.display()
        );
    }

    let zbc_str = zbc_path.to_str().expect("zbc path is utf-8");
    let artifact = z42_vm::metadata::load_artifact(zbc_str)
        .unwrap_or_else(|e| panic!("load_artifact({zbc_str}) failed: {e}"));
    let module = artifact.module;

    // Pre-register Counter inside a VmGuard scope so `z42_register_type`
    // can locate the active VM. The VM owns the type table; the call has
    // to happen on this thread before we run any IR that references it.
    let ctx = VmContext::new();
    invoke_with_vm_guard(&ctx, || unsafe { numz42_register_static() });

    // Find the user `Main` function. Compiler emits it under the namespace
    // declared in source.z42 (`Demo.Main` here).
    let main_idx = module
        .functions
        .iter()
        .position(|f| {
            f.name == "Demo.Main" || f.name.ends_with(".Main") || f.name == "Main"
        })
        .unwrap_or_else(|| {
            let names: Vec<&String> = module.functions.iter().map(|f| &f.name).collect();
            panic!("no Main fn; functions present: {names:?}")
        });
    let main_fn = &module.functions[main_idx];

    let result = z42_vm::interp::run_returning(&ctx, &module, main_fn, &[] as &[Value])
        .expect("Main runs to completion");
    assert_eq!(
        result,
        Some(Value::I64(3)),
        "z42 Main should return Counter::get after 3 increments"
    );
}

#[test]
fn callnative_unknown_method_z0905() {
    let ctx = vm_with_counter_registered();
    let m = build_module(
        "unknown_method_e2e",
        vec![Instruction::CallNative {
            dst: 0,
            module: "numz42".into(),
            type_name: "Counter".into(),
            symbol: "ghost_method".into(),
            args: vec![],
        }],
        Terminator::Ret { reg: None },
    );
    let func = &m.functions[0];
    let err = z42_vm::interp::run(&ctx, &m, func, &[] as &[Value]).expect_err("must fail");
    assert!(format!("{err:#}").contains("ghost_method"));
    assert!(format!("{err:#}").contains("Z0905"));
}
