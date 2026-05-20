//! Cross-thread smoke tests for the Phase 5.2 multi-threading foundation
//! (`docs/spec/archive/2026-05-20-add-multithreading-foundation/`).
//!
//! These integration tests don't run any z42 code — they just verify the
//! Rust type tree (`VmContext` / `VmCore` / `GcRef` / `MagrGC`) crosses an
//! `std::thread::spawn` boundary and works correctly from a worker thread.
//!
//! This is the **end-to-end Send/Sync proof** that complements the
//! compile-time `assert_send_sync` assertions in
//! `src/runtime/src/gc/arc_heap_tests/send_sync.rs`. If a future change
//! breaks Send-safety at the link level (e.g., a trait object that compiles
//! standalone but can't actually be moved at runtime), these tests will
//! catch it.

use std::sync::Arc;
use std::thread;

use z42::corelib;
use z42::gc::{ArcMagrGC, MagrGC};
use z42::metadata::bytecode::{BasicBlock, Function, Module, Terminator};
use z42::metadata::types::ExecMode;
use z42::metadata::Value;
use z42::vm_context::VmContext;

#[test]
fn vm_context_alloc_array_then_read_on_worker_thread() {
    // Construct a VmContext on the main thread, allocate an array, then
    // hand the GC handle to a worker thread and verify it can read the
    // contents. Tests: VmContext is Send + Sync; the Arc<VmCore> can be
    // cloned across threads; GcRef<Vec<Value>> values move correctly.
    let ctx = VmContext::new();
    let arr_val = ctx.heap().alloc_array(vec![
        Value::I64(42),
        Value::I64(100),
    ]);

    let arr_gc = match arr_val {
        Value::Array(g) => g,
        other => panic!("expected Value::Array, got {other:?}"),
    };

    let handle = thread::spawn(move || {
        let guard = arr_gc.borrow();
        assert_eq!(guard.len(), 2);
        match (&guard[0], &guard[1]) {
            (Value::I64(a), Value::I64(b)) => (*a, *b),
            other => panic!("unexpected values: {other:?}"),
        }
    });
    let (a, b) = handle.join().expect("worker panicked");
    assert_eq!(a, 42);
    assert_eq!(b, 100);
}

// Note: VmCore field access from integration tests would require `pub fn core(&self)`
// accessor on VmContext. Compile-time `assert_send_sync::<VmCore>()` in
// `gc/arc_heap_tests/send_sync.rs` already proves the type-level Send+Sync;
// this integration suite verifies the *handle* surface (GcRef, Box<dyn MagrGC>,
// VmContext) crosses threads correctly, which is the user-visible contract.

#[test]
fn gc_alloc_on_one_thread_drop_on_another() {
    // Allocate on main, last-strong-reference drops on worker. Validates
    // that GcRef Drop is thread-safe.
    let ctx = VmContext::new();
    let val = ctx.heap().alloc_object(
        dummy_type_desc("DemoCrossThreadObj"),
        vec![Value::I64(0)],
        z42::metadata::NativeData::None,
    );
    let handle = thread::spawn(move || {
        // Move val into worker; drop happens at end of scope (last strong
        // reference, since main released it via move).
        drop(val);
    });
    handle.join().expect("worker panicked");
    // No assertion needed — if Drop panics or deadlocks the join fails.
}

#[test]
fn box_dyn_magr_gc_works_on_worker_thread() {
    // Confirms `Box<dyn MagrGC>` (the type VmCore holds) can move across
    // threads and dispatch methods. Box<dyn MagrGC> requires Send + Sync
    // which Phase 3 of the spec just landed.
    let heap: Box<dyn MagrGC> = Box::new(ArcMagrGC::new());
    let handle = thread::spawn(move || {
        let _ = heap.alloc_array(vec![Value::I64(123)]);
        heap.used_bytes()
    });
    let bytes = handle.join().expect("worker panicked");
    assert!(bytes > 0, "alloc_array should bump used_bytes");
}

// ── add-threading-stdlib Phase 4.4 (2026-05-20) ──────────────────────────────

#[test]
fn spawn_via_builtin_then_join_runs_action_on_worker_thread() {
    // End-to-end test: hand `__thread_spawn` a `Value::FuncRef` to a minimal
    // function (empty body + Ret void), then `__thread_join` and assert the
    // success discriminator. Proves the full plumbing — Arc<VmCore> sharing,
    // new_with_core construction on the worker, exec_function dispatch, and
    // JoinHandle propagation through the threads registry.
    let module = make_void_action_module("TestVoidAction");
    let ctx = VmContext::with_module(module);

    let action = Value::FuncRef("TestVoidAction".into());
    let spawn_result = corelib::threading::builtin_thread_spawn(&ctx, &[action])
        .expect("__thread_spawn should succeed for a valid FuncRef");
    let slot_id = match spawn_result {
        Value::I64(n) => n,
        other => panic!("expected Value::I64 slot id, got {other:?}"),
    };
    assert!(slot_id > 0, "slot id should be > 0 (1-based)");

    let join_result = corelib::threading::builtin_thread_join(&ctx, &[Value::I64(slot_id)])
        .expect("__thread_join should not error for a known slot");
    let arr = match join_result {
        Value::Array(rc) => rc,
        other => panic!("expected Value::Array, got {other:?}"),
    };
    let borrowed = arr.borrow();
    assert!(matches!(borrowed[0], Value::I64(0)),
        "expected JOIN_OK (0), got {:?}", borrowed[0]);

    // Second join on the same slot → JOIN_UNKNOWN_SLOT (handle consumed).
    let second = corelib::threading::builtin_thread_join(&ctx, &[Value::I64(slot_id)]).unwrap();
    if let Value::Array(rc) = second {
        let b = rc.borrow();
        assert!(matches!(b[0], Value::I64(2)),
            "second join on same slot should be JOIN_UNKNOWN_SLOT (2), got {:?}", b[0]);
    } else {
        panic!("expected Array, got {second:?}");
    }
}

// ── helpers ──────────────────────────────────────────────────────────────────

/// Build a Module containing one zero-arg, void-returning function whose
/// body is empty + `Ret { reg: None }`. Sufficient for spawn/join smoke
/// tests where we just need a valid dispatch target.
fn make_void_action_module(fn_name: &str) -> Module {
    let func = Function {
        name:                   fn_name.to_string(),
        param_count:            0,
        param_types:            vec![],
        ret_type:               "void".into(),
        exec_mode:              ExecMode::Interp,
        blocks: vec![BasicBlock {
            label:        "entry".into(),
            instructions: vec![],
            terminator:   Terminator::Ret { reg: None },
        }],
        exception_table:        vec![],
        is_static:              true,
        max_reg:                1,
        line_table:             vec![],
        local_vars:             vec![],
        type_params:            vec![],
        type_param_constraints: vec![],
        block_index:            std::collections::HashMap::new(),
        resolved:               std::sync::OnceLock::new(),
    };
    let mut func_index = std::collections::HashMap::new();
    func_index.insert(fn_name.to_string(), 0);

    Module {
        name:                fn_name.to_string(),
        string_pool:         vec![],
        classes:             vec![],
        functions:           vec![func],
        type_registry:       std::collections::HashMap::new(),
        type_registry_vec:   Vec::new(),
        func_index,
        func_ref_cache_slots: 0,
    }
}

fn dummy_type_desc(name: &str) -> Arc<z42::metadata::TypeDesc> {
    Arc::new(z42::metadata::TypeDesc {
        name: name.to_string(),
        base_name: None,
        fields: Vec::new(),
        field_index: std::collections::HashMap::new(),
        vtable: Vec::new(),
        vtable_index: std::collections::HashMap::new(),
        own_fields: Vec::new(),
        own_methods: Vec::new(),
        type_params: vec![],
        type_args: vec![],
        type_param_constraints: vec![],
        id: z42::metadata::tokens::TypeId::UNRESOLVED,
    })
}
