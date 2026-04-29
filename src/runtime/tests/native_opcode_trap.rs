//! C1-scaffold trap behaviour: the four native interop opcodes are decoded
//! cleanly but always trap with a clear error pointing at the implementing
//! spec when actually executed.
//!
//! Once specs C2/C4/C5 land, the corresponding test below should be flipped
//! from "expects trap" to "expects success".

use std::collections::HashMap;

use z42_vm::metadata::{
    BasicBlock, ExecMode, Function, Instruction, Module, Terminator, Value,
};
use z42_vm::vm_context::VmContext;

fn module_with_single_instr(name: &str, instr: Instruction) -> Module {
    let func = Function {
        name: format!("{name}.Main"),
        param_count: 0,
        ret_type: "void".to_string(),
        exec_mode: ExecMode::Interp,
        blocks: vec![BasicBlock {
            label: "entry".to_string(),
            instructions: vec![instr],
            terminator: Terminator::Ret { reg: None },
        }],
        exception_table: vec![],
        is_static: true,
        max_reg: 4,
        line_table: vec![],
        local_vars: vec![],
        type_params: vec![],
        type_param_constraints: vec![],
        block_index: HashMap::new(),
    };

    Module {
        name: name.to_string(),
        string_pool: vec![],
        classes: vec![],
        functions: vec![func],
        type_registry: HashMap::new(),
        func_index: HashMap::new(),
    }
}

fn run(module: &Module) -> anyhow::Result<()> {
    let ctx = VmContext::new();
    let func = &module.functions[0];
    z42_vm::interp::run(&ctx, module, func, &[] as &[Value])
}

fn assert_trap_with(err: anyhow::Error, fragment: &str) {
    let msg = format!("{err:#}");
    assert!(
        msg.contains(fragment),
        "expected error to contain `{fragment}`, got: {msg}"
    );
}

#[test]
fn call_native_unknown_type_z0905() {
    // C2 (`impl-tier1-c-abi`) flipped CallNative from a blanket trap to
    // real registry+libffi dispatch; the failure mode now is "unknown
    // native type" because no library has registered numz42::Tensor.
    let m = module_with_single_instr(
        "call_native_unknown_type",
        Instruction::CallNative {
            dst: 0,
            module: "numz42".into(),
            type_name: "Tensor".into(),
            symbol: "__shim_Tensor_dot".into(),
            args: vec![],
        },
    );
    let err = run(&m).expect_err("CallNative must fail when type is unregistered");
    assert_trap_with(err, "Z0905");
}

#[test]
fn call_native_vtable_traps_with_spec_pointer() {
    let m = module_with_single_instr(
        "call_native_vtable_test",
        Instruction::CallNativeVtable {
            dst: 0,
            recv: 1,
            vtable_slot: 7,
            args: vec![],
        },
    );
    let err = run(&m).expect_err("CallNativeVtable must trap in C1");
    assert_trap_with(err, "spec C5");
}

#[test]
fn pin_ptr_traps_with_spec_pointer() {
    let m = module_with_single_instr(
        "pin_ptr_test",
        Instruction::PinPtr { dst: 0, src: 1 },
    );
    let err = run(&m).expect_err("PinPtr must trap in C1");
    assert_trap_with(err, "spec C4");
}

#[test]
fn unpin_ptr_traps_with_spec_pointer() {
    let m = module_with_single_instr(
        "unpin_ptr_test",
        Instruction::UnpinPtr { pinned: 1 },
    );
    let err = run(&m).expect_err("UnpinPtr must trap in C1");
    assert_trap_with(err, "spec C4");
}
