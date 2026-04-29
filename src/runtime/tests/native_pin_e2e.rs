//! Spec C4 end-to-end: PinPtr / UnpinPtr / FieldGet on PinnedView.
//!
//! Hand-crafted IR drives the pin pipeline in the absence of the user-side
//! `pinned p = s { ... }` syntax (which lands in C5). Each test verifies
//! one slice of the runtime: construction, field projection, error path.

use std::collections::HashMap;

use z42_vm::metadata::{
    BasicBlock, ExecMode, Function, Instruction, Module, Terminator, Value,
};
use z42_vm::vm_context::VmContext;

fn build_module(name: &str, instructions: Vec<Instruction>, terminator: Terminator) -> Module {
    let func = Function {
        name: format!("{name}.Main"),
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
        max_reg: 8,
        line_table: vec![],
        local_vars: vec![],
        type_params: vec![],
        type_param_constraints: vec![],
        block_index: HashMap::new(),
    };
    Module {
        name: name.to_string(),
        string_pool: vec!["hello world".to_string()],
        classes: vec![],
        functions: vec![func],
        type_registry: HashMap::new(),
        func_index: HashMap::new(),
    }
}

fn run_returning(module: &Module) -> anyhow::Result<Option<Value>> {
    let ctx = VmContext::new();
    let func = &module.functions[0];
    z42_vm::interp::run_returning(&ctx, module, func, &[] as &[Value])
}

#[test]
fn pin_str_then_field_len_returns_byte_length() {
    let m = build_module(
        "pin_field_len",
        vec![
            Instruction::ConstStr { dst: 0, idx: 0 },           // r0 = "hello world"
            Instruction::PinPtr { dst: 1, src: 0 },             // r1 = PinnedView{...}
            Instruction::FieldGet { dst: 2, obj: 1, field_name: "len".to_string() },
            Instruction::UnpinPtr { pinned: 1 },
        ],
        Terminator::Ret { reg: Some(2) },
    );
    let out = run_returning(&m).expect("pin field len ok");
    assert_eq!(out, Some(Value::I64("hello world".len() as i64)));
}

#[test]
fn pin_str_then_field_ptr_returns_nonzero() {
    let m = build_module(
        "pin_field_ptr",
        vec![
            Instruction::ConstStr { dst: 0, idx: 0 },
            Instruction::PinPtr { dst: 1, src: 0 },
            Instruction::FieldGet { dst: 2, obj: 1, field_name: "ptr".to_string() },
            Instruction::UnpinPtr { pinned: 1 },
        ],
        Terminator::Ret { reg: Some(2) },
    );
    let out = run_returning(&m).expect("pin field ptr ok");
    let Some(Value::I64(ptr)) = out else {
        panic!("expected I64, got {out:?}");
    };
    assert!(ptr != 0, "PinPtr must produce a non-NULL ptr for a non-empty String");
}

#[test]
fn pin_view_unknown_field_z0908() {
    let m = build_module(
        "pin_unknown_field",
        vec![
            Instruction::ConstStr { dst: 0, idx: 0 },
            Instruction::PinPtr { dst: 1, src: 0 },
            Instruction::FieldGet { dst: 2, obj: 1, field_name: "lulz".to_string() },
        ],
        Terminator::Ret { reg: Some(2) },
    );
    let err = run_returning(&m).expect_err("unknown PinnedView field must fail");
    let msg = format!("{err:#}");
    assert!(msg.contains("Z0908"), "msg = {msg}");
    assert!(msg.contains("PinnedView"), "msg = {msg}");
    assert!(msg.contains("lulz"), "msg = {msg}");
}

#[test]
fn pin_array_z0908_pending_buffer_type() {
    // Array<u8> pinning waits on a dedicated byte-buffer Value variant;
    // the runtime should reject Array sources today with a clear message
    // pointing at the follow-up spec.
    let m = build_module(
        "pin_array",
        vec![
            Instruction::ArrayNewLit {
                dst: 0,
                elems: vec![],
            },
            Instruction::PinPtr { dst: 1, src: 0 },
        ],
        Terminator::Ret { reg: None },
    );
    let err = run_returning(&m).expect_err("Array PinPtr must fail in C4");
    let msg = format!("{err:#}");
    assert!(msg.contains("Z0908"), "msg = {msg}");
    assert!(msg.contains("Array"), "msg = {msg}");
}

#[test]
fn unpin_then_str_still_usable() {
    // PinPtr / UnpinPtr is zero-cost on the RC backend; the source string
    // remains accessible afterwards.
    let m = build_module(
        "pin_unpin_then_use",
        vec![
            Instruction::ConstStr { dst: 0, idx: 0 },
            Instruction::PinPtr { dst: 1, src: 0 },
            Instruction::UnpinPtr { pinned: 1 },
            Instruction::FieldGet { dst: 2, obj: 0, field_name: "Length".to_string() },
        ],
        Terminator::Ret { reg: Some(2) },
    );
    let out = run_returning(&m).expect("post-unpin String access ok");
    assert_eq!(out, Some(Value::I64("hello world".chars().count() as i64)));
}
