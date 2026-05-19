#![allow(dangerous_implicit_autorefs)]
//! Array allocation, element access, length.

use crate::metadata::types::default_value_for_tag;
use crate::metadata::Value;
use super::super::frame::{JitFrame, JitModuleCtx};
use super::{set_exception, vm_ctx_ref};

#[no_mangle]
pub unsafe extern "C" fn jit_array_new(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, size: u32, elem_tag: u8,
) -> u8 {
    let n = match &(*frame).regs[size as usize] {
        Value::I64(n) if *n >= 0 => *n as usize,
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArrayNew: expected non-negative int, got {:?}", other)));
            return 1;
        }
    };
    let default = default_value_for_tag(elem_tag);
    (*frame).regs[dst as usize] = vm_ctx_ref(ctx).heap().alloc_array(vec![default; n]);
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_new_lit(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, elems_ptr: *const u32, elem_cnt: usize,
) {
    let elems = std::slice::from_raw_parts(elems_ptr, elem_cnt);
    let vals: Vec<Value> = elems.iter().map(|&r| (*frame).regs[r as usize].clone()).collect();
    (*frame).regs[dst as usize] = vm_ctx_ref(ctx).heap().alloc_array(vals);
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_get(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, arr: u32, idx: u32,
) -> u8 {
    let arr_val = (*frame).regs[arr as usize].clone();
    let idx_val = (*frame).regs[idx as usize].clone();
    let result = match &arr_val {
        Value::Array(rc) => {
            let i = match &idx_val {
                Value::I64(n) if *n >= 0 => *n as usize,
                Value::I64(n) if *n >= 0 => *n as usize,
                other => {
                    set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArrayGet: bad index {:?}", other)));
                    return 1;
                }
            };
            let borrowed = rc.borrow();
            if i >= borrowed.len() {
                set_exception(vm_ctx_ref(ctx), Value::Str(format!("array index {} out of bounds (len={})", i, borrowed.len())));
                return 1;
            }
            borrowed[i].clone()
        }
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArrayGet: expected array, got {:?}", other)));
            return 1;
        }
    };
    (*frame).regs[dst as usize] = result;
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_set(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    arr: u32, idx: u32, val: u32,
) -> u8 {
    let arr_val = (*frame).regs[arr as usize].clone();
    let idx_val = (*frame).regs[idx as usize].clone();
    let v       = (*frame).regs[val as usize].clone();
    match &arr_val {
        Value::Array(rc) => {
            let i = match &idx_val {
                Value::I64(n) if *n >= 0 => *n as usize,
                Value::I64(n) if *n >= 0 => *n as usize,
                other => {
                    set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArraySet: bad index {:?}", other)));
                    return 1;
                }
            };
            let mut borrowed = rc.borrow_mut();
            if i >= borrowed.len() {
                set_exception(vm_ctx_ref(ctx), Value::Str(format!("array index {} out of bounds (len={})", i, borrowed.len())));
                return 1;
            }
            borrowed[i] = v;
        }
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArraySet: expected array, got {:?}", other)));
            return 1;
        }
    }
    0
}

#[no_mangle]
pub unsafe extern "C" fn jit_array_len(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, arr: u32,
) -> u8 {
    match &(*frame).regs[arr as usize] {
        Value::Array(rc) => { (*frame).regs[dst as usize] = Value::I64(rc.borrow().len() as i64); 0 }
        other => {
            set_exception(vm_ctx_ref(ctx), Value::Str(format!("ArrayLen: expected array, got {:?}", other)));
            1
        }
    }
}
