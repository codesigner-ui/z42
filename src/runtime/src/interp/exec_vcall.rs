/// Virtual dispatch (`VCall`) plus its dedicated helpers.
///
/// `VCall` is split out from `exec_object.rs` because it carries:
///   • The L3-G4b primitive-as-struct dispatch path (Value::I64 → `Std.Int32.<m>`)
///   • The `add-array-base-class` is-a hardcoded chain for `Value::Array`
///   • A 3-way fallback search (vtable_index → resolve_virtual → lazy hierarchy walk)
/// Together ~140 LOC; keeping it isolated makes the rest of `exec_object.rs`
/// fit comfortably under the file-size soft limit.

use crate::metadata::{Module, Value};
use crate::vm_context::VmContext;
use anyhow::{bail, Result};
use std::sync::Arc;

use super::dispatch::resolve_virtual;
use super::ops::collect_args;
use super::{ExecOutcome, Frame};

/// L3-G4b primitive-as-struct: maps a primitive `Value` variant to its stdlib
/// struct's qualified class name (e.g. `Value::I64` → `"Std.Int32"`). The VM
/// dispatches primitive method calls by constructing `{class}.{method}` and
/// looking up the function in `module.func_index` — replacing the old
/// hardcoded `(Value, method)` → builtin-name switch.
///
/// Returns None for non-primitive values (objects, null, etc.).
pub(crate) fn primitive_class_name(obj: &Value) -> Option<&'static str> {
    use crate::metadata::well_known_names::*;
    match obj {
        // rename-primitives-to-pascal-case (2026-05-24): VM dispatch on
        // Value::I64 routes to Std.Int32 by default (narrow int / long values
        // are tagged with class FQN at compile-time in VCall instructions).
        Value::I64(_)  => Some(STD_INT32),
        Value::F64(_)  => Some(STD_DOUBLE),
        Value::Bool(_) => Some(STD_BOOLEAN),
        Value::Char(_) => Some(STD_CHAR),
        Value::Str(_)  => Some(STD_STRING),
        // 2026-05-07 add-array-base-class: T[] dispatches to Std.Array methods
        // (Clone / GetType / ToString / Equals / GetHashCode). The lookup path
        // below tries `Std.Array.<method>` first, then falls through to base
        // `Std.Object.<method>` via the existing primitive overload retry logic.
        Value::Array(_) => Some(STD_ARRAY),
        _ => None,
    }
}

/// refactor-vcall-ic-primitives (2026-05-17): synthetic TypeId for IC keying.
/// Primitives don't have a real `TypeDesc.id` (they're built-in runtime values,
/// not user-defined classes). Returning a stable `PRIM_TYPE_*` lets `VCallIC`
/// cache them with the same `cached_type_id` mechanism used for object
/// receivers — no extra slot, no separate cache path.
///
/// Returns None for objects (which use real `type_desc.id.0`) and `Value::Null`.
#[inline]
pub(crate) fn value_synthetic_type_id(obj: &Value) -> Option<u32> {
    use crate::metadata::tokens::*;
    match obj {
        Value::I64(_)   => Some(PRIM_TYPE_I64),
        Value::F64(_)   => Some(PRIM_TYPE_F64),
        Value::Bool(_)  => Some(PRIM_TYPE_BOOL),
        Value::Char(_)  => Some(PRIM_TYPE_CHAR),
        Value::Str(_)   => Some(PRIM_TYPE_STR),
        Value::Array(_) => Some(PRIM_TYPE_ARRAY),
        _ => None,
    }
}

/// 2026-05-07 add-array-base-class: hardcoded is-a check for `Value::Array`.
/// Class name comparison accepts both unqualified and `Std.`-qualified forms
/// because IR-emitted class names depend on TypeChecker's qualification path
/// (imported classes use FQ; bare references unqualified).
pub(super) fn is_array_isa(class_name: &str) -> bool {
    matches!(class_name, "Array" | "Object" | "Std.Array" | "Std.Object")
}

pub(super) fn vcall(
    ctx: &VmContext, module: &Module, frame: &mut Frame,
    dst: u32, obj: u32, method: &str, args: &[u32],
    // vcall_ic: monomorphic inline cache (TypeId, vtable slot, MethodId)
    // populated on first dispatch with this receiver type at this site.
    // Subsequent hits with the same receiver type take the fast path
    // (single u32 compare + direct module.functions index).
    vcall_ic: Option<&crate::metadata::resolver::VCallIC>,
) -> Result<Option<Value>> {
    use std::sync::atomic::Ordering;
    let obj_val = frame.get(obj)?.clone();
    let mut extra_args = collect_args(&frame.regs, args)?;

    // ── Fast path: IC hit (object OR primitive receiver) ────────────────
    // Fires when (1) caller passed an IC, (2) receiver's TypeId — real for
    // Value::Object, synthetic `PRIM_TYPE_*` for primitives (refactor-vcall-
    // ic-primitives, 2026-05-17) — matches cache, (3) cached MethodId
    // resolves to a module function. Anything else falls through to the slow
    // path, which also updates the IC for next time.
    if let Some(ic) = vcall_ic {
        let recv_type = match &obj_val {
            Value::Object(rc) => rc.borrow().type_desc.id.0,
            other => value_synthetic_type_id(other)
                .unwrap_or(crate::metadata::tokens::UNRESOLVED),
        };
        let cached_type = ic.cached_type_id.load(Ordering::Relaxed);
        if recv_type != crate::metadata::tokens::UNRESOLVED && recv_type == cached_type {
            let fn_idx = ic.cached_fn_idx.load(Ordering::Relaxed);
            if fn_idx != crate::metadata::tokens::UNRESOLVED {
                if let Some(callee) = module.functions.get(fn_idx as usize) {
                    let mut call_args = vec![obj_val.clone()];
                    call_args.append(&mut extra_args);
                    let outcome = super::exec_function(ctx, module, callee, &call_args)?;
                    return match outcome {
                        ExecOutcome::Returned(ret) => {
                            frame.set(dst, ret.unwrap_or(Value::Null));
                            Ok(None)
                        }
                        ExecOutcome::Thrown(val) => Ok(Some(val)),
                    };
                }
            }
        }
    }

    // L3-G4b primitive-as-struct: primitives dispatch through their stdlib
    // struct's method (e.g. `Value::I64.CompareTo` → call `Std.Int32.CompareTo`
    // IR function, which contains a BuiltinInstr for `__int32_compare_to`).
    // This replaces the old hardcoded `(Value, method) → builtin` table —
    // method-to-native binding is now entirely data-driven via stdlib source.
    //
    // Overload resolution: when the receiver type is statically `object`
    // (e.g. `Std.Assert.Equal(object, object)` calling `expected.Equals(actual)`),
    // the C# emit can't pick an overload at compile time; the IR carries the
    // unmangled method name `Equals`. But IrGen emits overloaded methods with
    // a `$N` arity suffix (e.g. `Std.String.Equals$1`). We retry with `$<arity>`
    // when the unmangled lookup misses — covers `Equals` (arity 1) and any
    // other overloaded primitive method without per-Value-type special cases.
    // This subsumes the legacy `Value::Str` hardcoded block (review2 §2.2).
    if let Some(class_name) = primitive_class_name(&obj_val) {
        let mut call_args = vec![obj_val.clone()];
        call_args.append(&mut extra_args);
        let arity = call_args.len() - 1; // exclude `this`
        let primary = format!("{}.{}", class_name, method);
        let overload = format!("{}.{}${}", class_name, method, arity);
        // refactor-vcall-ic-primitives (2026-05-17): on intra-module resolve,
        // populate VCallIC so the next call at this site with the same primitive
        // receiver type takes the IC fast path above — skips both format!()
        // calls + the HashMap lookup. Cross-zpkg (lazy_fn) skips populate
        // because IC cached_fn_idx must index into THIS module's functions table.
        for func_name in [primary.as_str(), overload.as_str()] {
            if let Some(&idx) = module.func_index.get(func_name) {
                if let Some(callee) = module.functions.get(idx) {
                    if let (Some(ic), Some(synth_id)) =
                        (vcall_ic, value_synthetic_type_id(&obj_val))
                    {
                        ic.cached_type_id.store(synth_id, Ordering::Relaxed);
                        ic.cached_fn_idx.store(idx as u32, Ordering::Relaxed);
                    }
                    let outcome = super::exec_function(ctx, module, callee, &call_args)?;
                    return match outcome {
                        ExecOutcome::Returned(ret) => {
                            frame.set(dst, ret.unwrap_or(Value::Null));
                            Ok(None)
                        }
                        ExecOutcome::Thrown(val) => Ok(Some(val)),
                    };
                }
            }
            if let Some(lazy_fn) = ctx.try_lookup_function(func_name) {
                let outcome = super::exec_function(ctx, module, lazy_fn.as_ref(), &call_args)?;
                return match outcome {
                    ExecOutcome::Returned(ret) => {
                        frame.set(dst, ret.unwrap_or(Value::Null));
                        Ok(None)
                    }
                    ExecOutcome::Thrown(val) => Ok(Some(val)),
                };
            }
        }
        // Restore args for fallback paths below (call_args consumed obj_val).
        extra_args = call_args.into_iter().skip(1).collect();
    }

    // O(1) vtable dispatch using pre-computed TypeDesc.
    let type_desc = match &obj_val {
        Value::Object(rc) => rc.borrow().type_desc.clone(),
        other => bail!("VCall: expected object, got {:?}", other),
    };
    // Try paths in order:
    //   1. vtable_index hit (fastest path; pre-built type descriptor)
    //   2. resolve_virtual: walk module.classes hierarchy looking up
    //      `<class>.<method>` in module.func_index at each level
    //   3. (NEW 2026-05-05) lazy hierarchy walk: same hierarchy traversal
    //      but using ctx.try_lookup_function — covers methods inherited
    //      from cross-zpkg base classes (e.g. `e.GetType()` when
    //      `e: Std.TestFailure` and `GetType` is on Std.Object in z42.core)
    //   4. fallback: `<most-derived>.<method>` (likely fails downstream)
    let mut call_args = vec![obj_val];
    call_args.append(&mut extra_args);

    let mut callee_module_idx: Option<usize> = None;
    let mut callee_lazy: Option<Arc<crate::metadata::Function>> = None;
    let mut chosen_name: Option<String> = None;

    if let Some(&slot) = type_desc.vtable_index.get(method) {
        let n = type_desc.vtable[slot].1.clone();
        if let Some(&idx) = module.func_index.get(n.as_str()) {
            callee_module_idx = Some(idx);
            // Populate IC for next time this site sees the same receiver type.
            // Only cache when receiver's TypeId is resolved (not for fallback
            // synthetic descriptors where id == UNRESOLVED).
            if let Some(ic) = vcall_ic {
                let recv_type = type_desc.id.0;
                if recv_type != crate::metadata::tokens::UNRESOLVED {
                    ic.cached_type_id.store(recv_type, Ordering::Relaxed);
                    ic.cached_slot.store(slot as u32, Ordering::Relaxed);
                    ic.cached_fn_idx.store(idx as u32, Ordering::Relaxed);
                }
            }
        } else if let Some(fn_) = ctx.try_lookup_function(&n) {
            callee_lazy = Some(fn_);
        }
        chosen_name = Some(n);
    }
    if callee_module_idx.is_none() && callee_lazy.is_none() {
        if let Ok(f) = resolve_virtual(module, &type_desc.name, method) {
            let n = f.name.clone();
            if let Some(&idx) = module.func_index.get(n.as_str()) {
                callee_module_idx = Some(idx);
            } else if let Some(fn_) = ctx.try_lookup_function(&n) {
                callee_lazy = Some(fn_);
            }
            chosen_name = Some(n);
        }
    }
    // Lazy hierarchy walk: walk type_desc's base chain via
    // module.classes, trying ctx.try_lookup_function at each level.
    // Critical for cross-zpkg inherited methods (Std.Object.GetType
    // accessed via Std.TestFailure receiver).
    if callee_module_idx.is_none() && callee_lazy.is_none() {
        let mut cur = type_desc.name.clone();
        loop {
            let candidate = format!("{}.{}", cur, method);
            if let Some(&idx) = module.func_index.get(candidate.as_str()) {
                callee_module_idx = Some(idx);
                chosen_name = Some(candidate);
                break;
            }
            if let Some(fn_) = ctx.try_lookup_function(&candidate) {
                callee_lazy = Some(fn_);
                chosen_name = Some(candidate);
                break;
            }
            // Walk base via either module.classes or the type_desc
            // we already loaded (may be in registry but missing from
            // module.classes when imported lazily).
            let next = module.classes.iter()
                .find(|c| c.name == cur)
                .and_then(|c| c.base_class.clone())
                .or_else(|| {
                    // type_desc only has immediate base; but if cur is
                    // its own name we know its base. For deeper levels
                    // we rely on module.classes being populated.
                    if cur == type_desc.name { type_desc.base_name.clone() } else { None }
                });
            match next {
                Some(b) => cur = b,
                None => break,
            }
        }
    }

    let func_name = chosen_name.unwrap_or_else(|| format!("{}.{}", type_desc.name, method));
    let outcome = if let Some(idx) = callee_module_idx {
        let callee = &module.functions[idx];
        super::exec_function(ctx, module, callee, &call_args)?
    } else if let Some(lazy_fn) = callee_lazy {
        super::exec_function(ctx, module, lazy_fn.as_ref(), &call_args)?
    } else {
        bail!("VCall: function `{}` not found", func_name);
    };
    match outcome {
        ExecOutcome::Returned(ret) => {
            frame.set(dst, ret.unwrap_or(Value::Null));
            Ok(None)
        }
        ExecOutcome::Thrown(val) => Ok(Some(val)),
    }
}
