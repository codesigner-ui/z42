/// Virtual dispatch (`VCall`) plus its dedicated helpers.
///
/// `VCall` is split out from `exec_object.rs` because it carries:
///   • The L3-G4b primitive-as-struct dispatch path (Value::I64 → `Std.int.<m>`)
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
/// struct's qualified class name (e.g. `Value::I64` → `"Std.int"`). The VM
/// dispatches primitive method calls by constructing `{class}.{method}` and
/// looking up the function in `module.func_index` — replacing the old
/// hardcoded `(Value, method)` → builtin-name switch.
///
/// Returns None for non-primitive values (objects, arrays, null, etc.).
pub(crate) fn primitive_class_name(obj: &Value) -> Option<&'static str> {
    use crate::metadata::well_known_names::*;
    match obj {
        Value::I64(_)  => Some(STD_INT),
        Value::F64(_)  => Some(STD_DOUBLE),
        Value::Bool(_) => Some(STD_BOOL),
        Value::Char(_) => Some(STD_CHAR),
        Value::Str(_)  => Some(STD_STRING),  // capitalised — stdlib retains `class String`
        // 2026-05-07 add-array-base-class: T[] dispatches to Std.Array methods
        // (Clone / GetType / ToString / Equals / GetHashCode). The lookup path
        // below tries `Std.Array.<method>` first, then falls through to base
        // `Std.Object.<method>` via the existing primitive overload retry logic.
        Value::Array(_) => Some(STD_ARRAY),
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
) -> Result<Option<Value>> {
    let obj_val = frame.get(obj)?.clone();
    let mut extra_args = collect_args(&frame.regs, args)?;

    // L3-G4b primitive-as-struct: primitives dispatch through their stdlib
    // struct's method (e.g. `Value::I64.CompareTo` → call `Std.int.CompareTo`
    // IR function, which contains a BuiltinInstr for `__int_compare_to`).
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
        for func_name in [primary.as_str(), overload.as_str()] {
            if let Some(&idx) = module.func_index.get(func_name) {
                if let Some(callee) = module.functions.get(idx) {
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
