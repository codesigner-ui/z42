#![allow(dangerous_implicit_autorefs)]
// JIT helpers — constants, copy, variable slots, string ops, control-flow.

use crate::corelib::convert::value_to_str;
use crate::metadata::Value;
use super::frame::{JitFrame, JitModuleCtx};
use super::helpers::{set_exception, take_exception, JitFn};

// ── Constants ────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_const_i32(frame: *mut JitFrame, dst: u32, val: i32) {
    (*frame).regs[dst as usize] = Value::I64(val as i64);
}

#[no_mangle]
pub unsafe extern "C" fn jit_const_i64(frame: *mut JitFrame, dst: u32, val: i64) {
    (*frame).regs[dst as usize] = Value::I64(val as i64);
}

#[no_mangle]
pub unsafe extern "C" fn jit_const_f64(frame: *mut JitFrame, dst: u32, val: f64) {
    (*frame).regs[dst as usize] = Value::F64(val);
}

#[no_mangle]
pub unsafe extern "C" fn jit_const_bool(frame: *mut JitFrame, dst: u32, val: u8) {
    (*frame).regs[dst as usize] = Value::Bool(val != 0);
}

#[no_mangle]
pub unsafe extern "C" fn jit_const_char(frame: *mut JitFrame, dst: u32, val: i32) {
    (*frame).regs[dst as usize] = Value::Char(char::from_u32(val as u32).unwrap_or('\0'));
}

#[no_mangle]
pub unsafe extern "C" fn jit_const_null(frame: *mut JitFrame, dst: u32) {
    (*frame).regs[dst as usize] = Value::Null;
}

#[no_mangle]
pub unsafe extern "C" fn jit_const_str(
    frame: *mut JitFrame,
    ctx:   *const JitModuleCtx,
    dst:   u32,
    idx:   u32,
) -> u8 {
    let ctx_ref = &*ctx;
    match ctx_ref.string_pool.get(idx as usize) {
        Some(s) => { (*frame).regs[dst as usize] = Value::Str(s.clone()); 0 }
        None => { set_exception(Value::Str(format!("string pool index {} out of range", idx))); 1 }
    }
}

// ── Copy ─────────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_copy(frame: *mut JitFrame, dst: u32, src: u32) {
    let v = (*frame).regs[src as usize].clone();
    (*frame).regs[dst as usize] = v;
}

// ── String ───────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_str_concat(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8 {
    match (&(*frame).regs[a as usize], &(*frame).regs[b as usize]) {
        (Value::Str(sa), Value::Str(sb)) => {
            (*frame).regs[dst as usize] = Value::Str(format!("{}{}", sa, sb));
            0
        }
        (va, vb) => {
            set_exception(Value::Str(format!("StrConcat: expected two strings, got {:?} and {:?}", va, vb)));
            1
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_to_str(
    frame: *mut JitFrame, ctx: *const JitModuleCtx, dst: u32, src: u32,
) -> u8 {
    let val = &(*frame).regs[src as usize];
    if let Value::Object(rc) = val {
        let type_desc = rc.borrow().type_desc.clone();
        let func_name_opt = type_desc.vtable_index.get("ToString")
            .map(|&slot| type_desc.vtable[slot].1.clone());
        if let Some(func_name) = func_name_opt {
            let ctx_ref = &*ctx;
            if let Some(entry) = ctx_ref.fn_entries.get(&func_name) {
                let mut callee = JitFrame::new(entry.max_reg, &[val.clone()]);
                let jit_fn: JitFn = std::mem::transmute(entry.ptr);
                let r = jit_fn(&mut callee, ctx);
                if r != 0 { callee.recycle(); return 1; }
                let s = match callee.ret.take() {
                    Some(Value::Str(s)) => s,
                    Some(ref other)     => value_to_str(other),
                    None                => String::new(),
                };
                callee.recycle();
                (*frame).regs[dst as usize] = Value::Str(s);
                return 0;
            }
        }
        match crate::corelib::exec_builtin("__obj_to_str", &[val.clone()]) {
            Ok(v) => { (*frame).regs[dst as usize] = Value::Str(match v { Value::Str(s) => s, ref o => value_to_str(o) }); }
            Err(e) => { set_exception(Value::Str(e.to_string())); return 1; }
        }
    } else {
        (*frame).regs[dst as usize] = Value::Str(value_to_str(val));
    }
    0
}

// ── Control-flow helpers ─────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "C" fn jit_get_bool(frame: *mut JitFrame, reg: u32) -> u8 {
    match &(*frame).regs[reg as usize] {
        Value::Bool(b) => if *b { 1 } else { 0 },
        other => { set_exception(Value::Str(format!("BrCond: expected bool, got {:?}", other))); 255 }
    }
}

#[no_mangle]
pub unsafe extern "C" fn jit_set_ret(frame: *mut JitFrame, reg: u32) {
    let v = (*frame).regs[reg as usize].clone();
    (*frame).ret = Some(v);
}

#[no_mangle]
pub unsafe extern "C" fn jit_throw(frame: *mut JitFrame, reg: u32) {
    let v = (*frame).regs[reg as usize].clone();
    set_exception(v);
}

#[no_mangle]
pub unsafe extern "C" fn jit_install_catch(frame: *mut JitFrame, catch_reg: u32) {
    let v = take_exception().unwrap_or(Value::Null);
    (*frame).regs[catch_reg as usize] = v;
}
