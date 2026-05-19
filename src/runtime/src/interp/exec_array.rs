/// Array instructions: allocation, element access, length.
/// All errors here are VM-internal (out-of-bounds, type mismatch); arrays
/// don't propagate user exceptions through these primitives.

use crate::metadata::types::default_value_for_tag;
use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::{bail, Result};

use super::ops::to_usize;
use super::Frame;

pub(super) fn array_new(ctx: &VmContext, frame: &mut Frame, dst: u32, size: u32, elem_tag: u8) -> Result<()> {
    let n = to_usize(frame.get(size)?, "ArrayNew size")?;
    let default = default_value_for_tag(elem_tag);
    frame.set(dst, ctx.heap().alloc_array(vec![default; n]));
    Ok(())
}

pub(super) fn array_new_lit(ctx: &VmContext, frame: &mut Frame, dst: u32, elems: &[u32]) -> Result<()> {
    let vals: Vec<Value> = elems.iter()
        .map(|r| frame.get(*r).map(|v| v.clone()))
        .collect::<Result<_>>()?;
    frame.set(dst, ctx.heap().alloc_array(vals));
    Ok(())
}

pub(super) fn array_get(frame: &mut Frame, dst: u32, arr: u32, idx: u32) -> Result<()> {
    let result = match frame.get(arr)? {
        Value::Array(rc) => {
            let rc = rc.clone();
            let i = to_usize(frame.get(idx)?, "ArrayGet index")?;
            let borrowed = rc.borrow();
            if i >= borrowed.len() {
                bail!("array index {} out of bounds (len={})", i, borrowed.len());
            }
            borrowed[i].clone()
        }
        other => bail!("ArrayGet: expected array, got {:?}", other),
    };
    frame.set(dst, result);
    Ok(())
}

pub(super) fn array_set(frame: &mut Frame, arr: u32, idx: u32, val: u32) -> Result<()> {
    let v = frame.get(val)?.clone();
    match frame.get(arr)? {
        Value::Array(rc) => {
            let rc = rc.clone();
            let i = to_usize(frame.get(idx)?, "ArraySet index")?;
            let mut borrowed = rc.borrow_mut();
            if i >= borrowed.len() {
                bail!("array index {} out of bounds (len={})", i, borrowed.len());
            }
            borrowed[i] = v;
            Ok(())
        }
        other => bail!("ArraySet: expected array, got {:?}", other),
    }
}

pub(super) fn array_len(frame: &mut Frame, dst: u32, arr: u32) -> Result<()> {
    let len = match frame.get(arr)? {
        Value::Array(rc) => rc.borrow().len() as i32,
        other => bail!("ArrayLen: expected array, got {:?}", other),
    };
    frame.set(dst, Value::I64(len as i64));
    Ok(())
}
