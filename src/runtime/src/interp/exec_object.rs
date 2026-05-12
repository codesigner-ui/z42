/// Object instructions excluding VCall (which lives in `exec_vcall.rs` due
/// to its size). Covers: ObjNew (allocate + ctor), FieldGet / FieldSet,
/// IsInstance / AsCast (runtime type checks), StaticGet / StaticSet.

use crate::metadata::{Module, NativeData, Value};
use crate::vm_context::VmContext;
use anyhow::{bail, Result};

use super::dispatch::{is_subclass_or_eq_td, make_fallback_type_desc};
use super::exec_vcall::is_array_isa;
use super::ops::collect_args;
use super::Frame;

/// `ObjNew` dispatch. Currently still goes through `module.type_registry`
/// (HashMap by name) since registry isn't a Vec-by-TypeId — the cache
/// `type_token` enables future fast-path + cross-zpkg observability:
/// when the slot starts as UNRESOLVED and the lazy loader resolves the
/// class, we write the resolved id back so subsequent diagnostics /
/// reflection see it.
pub(super) fn obj_new(
    ctx: &VmContext, module: &Module, frame: &mut Frame,
    dst: u32, class_name: &str, ctor_name: &str, args: &[u32], type_args: &[String],
    type_token: Option<&std::sync::atomic::AtomicU32>,
) -> Result<()> {
    use std::sync::atomic::Ordering;
    // L3-G4d: for imported classes (e.g. Std.Collections.Stack) the TypeDesc
    // may only exist in the lazy loader until first use; probe it before
    // falling back to a blank synthetic descriptor.
    let type_desc = module.type_registry
        .get(class_name)
        .cloned()
        .or_else(|| ctx.try_lookup_type(class_name))
        .unwrap_or_else(|| {
            std::sync::Arc::new(make_fallback_type_desc(module, class_name))
        });

    // Refresh the type_token cache if it was UNRESOLVED at load (cross-zpkg
    // lazy class). Not strictly needed for current dispatch (we still go
    // through type_registry lookup above) but gives forward observability
    // and prepares the slot for Phase X where ObjNew may use TypeId-keyed
    // caches.
    if let Some(slot) = type_token {
        if slot.load(Ordering::Relaxed) == crate::metadata::tokens::UNRESOLVED
            && type_desc.id.is_resolved()
        {
            slot.store(type_desc.id.0, Ordering::Relaxed);
        }
    }

    // 2026-05-02 fix-class-field-default-init: 按字段声明类型选默认值
    // （int → I64(0)、bool → Bool(false)、str/ref → Null …），不再
    // 一律 Null。有显式 init 的字段在 ctor 入口被 FieldSet 覆写。
    let slots: Vec<Value> = type_desc.fields.iter()
        .map(|f| crate::metadata::default_value_for(&f.type_tag))
        .collect();
    let obj_val = ctx.heap().alloc_object(type_desc, slots, NativeData::None);

    // 2026-05-07 add-default-generic-typeparam (D-8b-3 Phase 2): populate
    // per-instance type_args from the IR instruction. Read by `DefaultOf`.
    if !type_args.is_empty() {
        if let Value::Object(ref rc) = obj_val {
            rc.borrow_mut().type_args = type_args.to_vec();
        }
    }

    // 直查 ctor_name (TypeChecker 已 overload-resolve)；无名字推断。
    // L3-G4d: fall back to lazy loader when the ctor lives in a stdlib zpkg
    // (imported generic class ctor isn't in the main module's function table).
    let ctor_fn = module.func_index.get(ctor_name)
        .and_then(|&i| module.functions.get(i));
    if let Some(ctor) = ctor_fn {
        let mut ctor_args = vec![obj_val.clone()];
        ctor_args.extend(collect_args(&frame.regs, args)?);
        super::exec_function(ctx, module, ctor, &ctor_args)?;
    } else if let Some(lazy_ctor) = ctx.try_lookup_function(ctor_name) {
        let mut ctor_args = vec![obj_val.clone()];
        ctor_args.extend(collect_args(&frame.regs, args)?);
        super::exec_function(ctx, module, lazy_ctor.as_ref(), &ctor_args)?;
    }

    frame.set(dst, obj_val);
    Ok(())
}

/// `FieldGet` dispatch with monomorphic inline cache. When `field_ic`
/// is provided and the receiver type matches the cached `TypeId`, the
/// field slot is fetched directly from `obj.slots[cached_slot]` (no hash).
/// On cache miss / first hit, walks `field_index` then writes back the
/// (TypeId, slot) pair so subsequent hits with the same receiver type
/// are fast. Polymorphic sites overwrite the slot each time (Phase 1
/// mono IC; Phase X may add poly).
///
/// Non-Object receivers (Str / Array / PinnedView) bypass the IC since
/// their field set is hardcoded (`Length` / `ptr` / `len`).
pub(super) fn field_get(
    frame: &mut Frame, dst: u32, obj: u32, field_name: &str,
    field_ic: Option<&crate::metadata::resolver::FieldIC>,
) -> Result<()> {
    use std::sync::atomic::Ordering;
    let val = match frame.get(obj)? {
        Value::Object(rc) => {
            let borrowed = rc.borrow();
            // IC fast path
            if let Some(ic) = field_ic {
                let recv_type = borrowed.type_desc.id.0;
                if recv_type != crate::metadata::tokens::UNRESOLVED
                    && ic.cached_type_id.load(Ordering::Relaxed) == recv_type
                {
                    let slot = ic.cached_slot.load(Ordering::Relaxed) as usize;
                    return {
                        let v = borrowed.slots.get(slot).cloned().unwrap_or(Value::Null);
                        drop(borrowed);
                        frame.set(dst, v);
                        Ok(())
                    };
                }
                // Miss: walk + update IC
                if let Some(&slot) = borrowed.type_desc.field_index.get(field_name) {
                    if recv_type != crate::metadata::tokens::UNRESOLVED {
                        ic.cached_type_id.store(recv_type, Ordering::Relaxed);
                        ic.cached_slot.store(slot as u32, Ordering::Relaxed);
                    }
                    borrowed.slots.get(slot).cloned().unwrap_or(Value::Null)
                } else {
                    Value::Null
                }
            } else if let Some(&slot) = borrowed.type_desc.field_index.get(field_name) {
                borrowed.slots.get(slot).cloned().unwrap_or(Value::Null)
            } else {
                Value::Null
            }
        }
        Value::Str(s) => match field_name {
            "Length" => Value::I64(s.chars().count() as i64),
            other    => bail!("string has no field `{}`", other),
        },
        Value::Array(rc) => match field_name {
            "Length" | "Count" => Value::I64(rc.borrow().len() as i64),
            other => bail!("array has no field `{}`", other),
        },
        Value::PinnedView { ptr, len, .. } => match field_name {
            // Spec C4 — only `ptr` / `len` are exposed; element type
            // information (kind) stays internal.
            "ptr" => Value::I64(*ptr as i64),
            "len" => Value::I64(*len as i64),
            other => bail!("PinnedView has no field `{}` (only `ptr` / `len`)", other),
        },
        other => bail!("FieldGet: not an object or known value type, got {:?}", other),
    };
    frame.set(dst, val);
    Ok(())
}

/// `FieldSet` dispatch — mirror of `field_get` IC pattern.
pub(super) fn field_set(
    frame: &mut Frame, obj: u32, field_name: &str, val: u32,
    field_ic: Option<&crate::metadata::resolver::FieldIC>,
) -> Result<()> {
    use std::sync::atomic::Ordering;
    let v = frame.get(val)?.clone();
    match frame.get(obj)? {
        Value::Object(rc) => {
            let mut borrowed = rc.borrow_mut();
            // IC fast path
            if let Some(ic) = field_ic {
                let recv_type = borrowed.type_desc.id.0;
                if recv_type != crate::metadata::tokens::UNRESOLVED
                    && ic.cached_type_id.load(Ordering::Relaxed) == recv_type
                {
                    let slot = ic.cached_slot.load(Ordering::Relaxed) as usize;
                    if slot < borrowed.slots.len() {
                        borrowed.slots[slot] = v;
                    }
                    return Ok(());
                }
                // Miss: walk + update IC
                let slot_opt = borrowed.type_desc.field_index.get(field_name).copied();
                if let Some(slot) = slot_opt {
                    if recv_type != crate::metadata::tokens::UNRESOLVED {
                        ic.cached_type_id.store(recv_type, Ordering::Relaxed);
                        ic.cached_slot.store(slot as u32, Ordering::Relaxed);
                    }
                    if slot < borrowed.slots.len() {
                        borrowed.slots[slot] = v;
                    }
                }
            } else if let Some(&slot) = borrowed.type_desc.field_index.get(field_name) {
                if slot < borrowed.slots.len() {
                    borrowed.slots[slot] = v;
                }
            }
            Ok(())
        }
        other => bail!("FieldSet: expected object, got {:?}", other),
    }
}

pub(super) fn is_instance(
    ctx: &VmContext, module: &Module, frame: &mut Frame, dst: u32, obj: u32, class_name: &str,
) -> Result<()> {
    let result = match frame.get(obj)? {
        Value::Object(rc) => {
            let runtime_class = rc.borrow().type_desc.name.clone();
            is_subclass_or_eq_td(ctx, &module.type_registry, &runtime_class, class_name)
        }
        // 2026-05-07 add-array-base-class: T[] is-a Std.Array is-a Std.Object.
        // VM hardcodes the chain since Value::Array doesn't carry a TypeDesc.
        Value::Array(_) => is_array_isa(class_name),
        Value::Null => false,
        _ => false,
    };
    frame.set(dst, Value::Bool(result));
    Ok(())
}

pub(super) fn as_cast(
    ctx: &VmContext, module: &Module, frame: &mut Frame, dst: u32, obj: u32, class_name: &str,
) -> Result<()> {
    let val = frame.get(obj)?.clone();
    let is_match = match &val {
        Value::Object(rc) => {
            let runtime_class = rc.borrow().type_desc.name.clone();
            is_subclass_or_eq_td(ctx, &module.type_registry, &runtime_class, class_name)
        }
        Value::Array(_) => is_array_isa(class_name),
        Value::Null => true,
        _ => false,
    };
    frame.set(dst, if is_match { val } else { Value::Null });
    Ok(())
}

/// `StaticGet` hot path. Resolver populates `static_field_tokens[site_idx]`
/// with the lazy-allocated `StaticFieldId` at module load (always succeeds).
/// `field_id` Some → direct Vec index (no hash); None → name fallback.
pub(super) fn static_get(
    ctx: &VmContext, frame: &mut Frame, dst: u32, field: &str,
    field_id: Option<u32>,
) {
    let v = match field_id {
        Some(id) => ctx.static_get_by_id(crate::metadata::tokens::StaticFieldId(id)),
        None     => ctx.static_get(field),
    };
    frame.set(dst, v);
}

pub(super) fn static_set(
    ctx: &VmContext, frame: &Frame, field: &str, val: u32,
    field_id: Option<u32>,
) -> Result<()> {
    let v = frame.get(val)?.clone();
    match field_id {
        Some(id) => ctx.static_set_by_id(crate::metadata::tokens::StaticFieldId(id), v),
        None     => ctx.static_set(field, v),
    }
    Ok(())
}
