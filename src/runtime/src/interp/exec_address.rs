/// Address-load instructions (spec impl-ref-out-in-runtime) and runtime
/// generic-default resolution.
///
/// Address-load: `LoadLocalAddr` / `LoadElemAddr` / `LoadFieldAddr` produce
/// `Value::Ref` pointing at the named location. Callers emit these for
/// `ref`/`out`/`in` arg expressions before the Call; the Ref flows through
/// `Call.args`; callee's `Frame::get_deref` / `set_thru_ref` transparently
/// follow it.
///
/// `DefaultOf` resolves `default(T)` for a generic type-parameter at runtime
/// (D-8b-3 Phase 2).

use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::{bail, Result};

use super::ops::to_usize;
use super::Frame;

pub(super) fn load_local_addr(ctx: &VmContext, frame: &mut Frame, dst: u32, slot: u32) {
    let depth = ctx.frame_stack_depth();
    // Current frame is the most recent push (depth - 1).
    let frame_idx = (depth.saturating_sub(1)) as u32;
    frame.set(dst, Value::Ref {
        kind: crate::metadata::types::RefKind::Stack { frame_idx, slot },
    });
}

pub(super) fn load_elem_addr(frame: &mut Frame, dst: u32, arr: u32, idx: u32) -> Result<()> {
    let arr_val = frame.get(arr)?;
    let idx_val = to_usize(frame.get(idx)?, "LoadElemAddr index")?;
    match arr_val {
        Value::Array(rc) => {
            frame.set(dst, Value::Ref {
                kind: crate::metadata::types::RefKind::Array {
                    gc_ref: rc.clone(), idx: idx_val,
                }
            });
            Ok(())
        }
        other => bail!("LoadElemAddr: expected array, got {:?}", other),
    }
}

pub(super) fn load_field_addr(frame: &mut Frame, dst: u32, obj: u32, field_name: &str) -> Result<()> {
    let obj_val = frame.get(obj)?;
    match obj_val {
        Value::Object(rc) => {
            frame.set(dst, Value::Ref {
                kind: crate::metadata::types::RefKind::Field {
                    gc_ref: rc.clone(),
                    field_name: field_name.to_string(),
                }
            });
            Ok(())
        }
        other => bail!("LoadFieldAddr: expected object, got {:?}", other),
    }
}

/// 2026-05-07 add-default-generic-typeparam (D-8b-3 Phase 2): runtime
/// resolution of `default(T)` where T is a generic type-parameter of the
/// receiver class. Reads `frame.regs[0]` (this) → `Object → instance.type_args[idx]`,
/// looks up the resolved tag via `default_value_for(tag)`, writes result to dst.
/// Non-Object reg 0 / OOB index / empty type_args → graceful Null.
/// type_args is per-instance (populated by `obj.new`), not per-TypeDesc, so
/// `Foo<int>` and `Foo<string>` instances differ at runtime despite sharing
/// the same TypeDesc Arc (z42 erasure with per-instance type-arg view).
pub(super) fn default_of(frame: &mut Frame, dst: u32, param_index: u8) {
    let val = match frame.get(0) {
        Ok(Value::Object(rc)) => {
            let borrowed = rc.borrow();
            borrowed.type_args.get(param_index as usize)
                .map(|tag| crate::metadata::types::default_value_for(tag))
                .unwrap_or(Value::Null)
        }
        _ => Value::Null,
    };
    frame.set(dst, val);
}
