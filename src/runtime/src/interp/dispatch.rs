/// Object dispatch helpers — vtable resolution, ToString protocol, type checks.
///
/// Static-field storage moved to `VmContext::static_fields` (consolidate-vm-state,
/// 2026-04-28). Call sites now use `ctx.static_get(field)` / `ctx.static_set(...)`.

use crate::metadata::{ClassDesc, FieldSlot, Function, Module, TypeDesc, Value};
use crate::vm_context::VmContext;
use anyhow::{bail, Result};
use std::collections::HashMap;

pub use crate::corelib::convert::value_to_str;

// ── Subclass check ───────────────────────────────────────────────────────────

/// Returns true if `derived` equals `target` or is a subclass via the TypeDesc registry.
///
/// Walks the base-class chain via the main module's `type_registry` first;
/// when a link is missing there falls back to `ctx.try_lookup_type` so
/// classes loaded lazily from imported zpkgs (e.g. `Std.TestFailure` in
/// z42.test) participate in cross-zpkg subclass checks. Without the
/// fallback `catch (Exception e)` failed to match a `TestFailure` thrown
/// across the z42.core → z42.test boundary.
pub fn is_subclass_or_eq_td(
    ctx: &VmContext,
    registry: &HashMap<String, std::sync::Arc<TypeDesc>>,
    derived: &str,
    target: &str,
) -> bool {
    let mut cur: String = derived.to_string();
    loop {
        if cur == target { return true; }
        let td = registry.get(cur.as_str()).cloned()
            .or_else(|| ctx.try_lookup_type(cur.as_str()));
        match td.and_then(|t| t.base_name.clone()) {
            Some(base) => cur = base,
            None => return false,
        }
    }
}

// ── ToString protocol ────────────────────────────────────────────────────────

/// Convert a value to its string representation, respecting `ToString()` overrides on objects.
///
/// For `Value::Object` we try to dispatch `ToString` via the vtable. If the class has no
/// `ToString` method (e.g. it inherits the default from `Std.Object`) we fall back to the
/// `__obj_to_str` builtin (simple name). All other value types use `value_to_str` directly.
pub fn obj_to_string(ctx: &VmContext, module: &Module, val: &Value) -> Result<String> {
    if let Value::Object(rc) = val {
        let type_desc = rc.borrow().type_desc.clone();
        // Try vtable first (O(1))
        let func_name_opt = type_desc.vtable_index.get("ToString")
            .map(|&slot| type_desc.vtable[slot].1.clone());
        if let Some(func_name) = func_name_opt {
            let callee = module.func_index.get(func_name.as_str())
                .and_then(|&idx| module.functions.get(idx));
            if let Some(callee) = callee {
                let outcome = super::exec_function(ctx, module, callee, &[val.clone()])?;
                return match outcome {
                    super::ExecOutcome::Returned(Some(Value::Str(s))) => Ok(s.to_string()),
                    super::ExecOutcome::Returned(Some(other))         => Ok(value_to_str(&other)),
                    super::ExecOutcome::Returned(None)                => Ok(String::new()),
                    super::ExecOutcome::Thrown(v)                     => Ok(format!("<exception: {}>", value_to_str(&v))),
                };
            }
        }
        // Fallback: builtin obj_to_str (unqualified type name)
        return crate::corelib::exec_builtin(
                ctx,
                crate::metadata::well_known_names::BUILTIN_OBJ_TO_STR,
                &[val.clone()])
            .map(|v| match v { Value::Str(s) => s.to_string(), other => value_to_str(&other) });
    }
    Ok(value_to_str(val))
}

// ── Virtual method resolution (fallback) ─────────────────────────────────────

/// Fallback linear walk used when TypeDesc is missing (e.g. stdlib stubs).
pub fn resolve_virtual<'m>(module: &'m Module, class_name: &str, method: &str) -> Result<&'m Function> {
    let mut cur = class_name;
    loop {
        let qualified = format!("{}.{}", cur, method);
        if let Some(f) = module.func_index.get(qualified.as_str()).and_then(|&i| module.functions.get(i)) {
            return Ok(f);
        }
        match module.classes.iter().find(|c| c.name == cur).and_then(|c| c.base_class.as_deref()) {
            Some(base) => cur = base,
            None => bail!("VCall: no implementation of `{}` in hierarchy of `{}`", method, class_name),
        }
    }
}

// ── Fallback TypeDesc ────────────────────────────────────────────────────────

/// Build a minimal TypeDesc from the ClassDesc chain — used when the registry
/// is absent (merged stdlib modules arrive without pre-built TypeDesc).
pub fn make_fallback_type_desc(module: &Module, class_name: &str) -> TypeDesc {
    let mut fields: Vec<FieldSlot> = Vec::new();
    let mut base_name: Option<String> = None;
    let mut cur = class_name;
    let mut chain: Vec<&ClassDesc> = Vec::new();
    loop {
        if let Some(desc) = module.classes.iter().find(|c| c.name == cur) {
            chain.push(desc);
            match &desc.base_class {
                Some(b) => { base_name = Some(b.clone()); cur = b.as_str(); }
                None    => break,
            }
        } else {
            break;
        }
    }
    for desc in chain.iter().rev() {
        for f in &desc.fields {
            if !fields.iter().any(|s: &FieldSlot| s.name == f.name) {
                fields.push(FieldSlot {
                    name: f.name.clone(),
                    type_tag: f.type_tag.clone(),
                });
            }
        }
    }
    let field_index = fields.iter().enumerate().map(|(i, f)| (f.name.clone(), i)).collect();
    // Fallback type — there's no separate "own vs inherited" split because
    // `chain` walked the inheritance chain by-name within this module. Mark
    // all fields as own (cross-zpkg fixup won't re-process this entry since
    // base resolution above is already complete).
    let own_fields = fields.clone();
    TypeDesc {
        name: class_name.to_string(),
        base_name,
        own_fields: own_fields.into(),
        own_methods: vec![].into(),
        fields,
        field_index,
        vtable: Vec::new(),
        vtable_index: HashMap::new(), type_params: vec![].into(), type_args: vec![].into(),
        type_param_constraints: vec![].into(),
        id: crate::metadata::tokens::TypeId::UNRESOLVED,
    }
}
