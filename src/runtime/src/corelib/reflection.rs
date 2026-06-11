//! Reflection builtins — read-only type introspection backing `Std.Type` and
//! `Std.Reflection.{FieldInfo,MethodInfo,ParameterInfo}` (add-reflection-mvp,
//! 2026-06-08).
//!
//! Design (see docs/spec/.../add-reflection-mvp/design.md):
//!   - `Std.Type` objects carry the real `Arc<TypeDesc>` in
//!     `NativeData::TypeHandle` (set by `__obj_get_type`). Reflection builtins
//!     read it to enumerate members.
//!   - Member/Type objects are populated EAGERLY: each builtin allocates the
//!     real z42 class (`Std.Reflection.FieldInfo`, …) via `try_lookup_type` and
//!     fills slots by name through `field_index`.
//!   - All builtins take the reflected object as `args[0]` and are LENIENT:
//!     a synthetic Type (primitive/array, no handle) yields empty arrays / null,
//!     never `bail!` (mirrors C# returning empty results).
//!   - Method signatures (params/return/static) are read on demand from the
//!     method's `Function` via `ctx.try_lookup_function` — no persisted
//!     per-type method table, no wire-format change.

use crate::metadata::{well_known_names, NativeData, TypeDesc, Value};
use crate::vm_context::VmContext;
use anyhow::Result;
use std::collections::HashSet;
use std::sync::Arc;

const STD_OBJECT: &str = "Std.Object";
const STD_REFLECTION_FIELDINFO: &str = "Std.Reflection.FieldInfo";
const STD_REFLECTION_METHODINFO: &str = "Std.Reflection.MethodInfo";
const STD_REFLECTION_PARAMINFO: &str = "Std.Reflection.ParameterInfo";
const STD_REFLECTION_PROPERTYINFO: &str = "Std.Reflection.PropertyInfo";

// ── Type-object construction ────────────────────────────────────────────────

/// Build a `Std.Type` object backed by the real `Std.Type` class (so its
/// reflection methods dispatch via the class vtable) and carrying `td` as
/// `NativeData::TypeHandle`. Falls back to a handle-less synthetic only if
/// z42.core's `Std.Type` isn't loaded (shouldn't happen in practice).
pub fn make_type_object(ctx: &VmContext, td: Arc<TypeDesc>) -> Value {
    let full = td.name.clone();
    let simple = full.rsplit('.').next().unwrap_or(&full).to_string();
    build_type(ctx, &simple, &full, NativeData::TypeHandle(td))
}

/// Build a `Std.Type` from a type name / type-tag string. Resolves to a real
/// handle when the name is a loaded class; otherwise yields a handle-less Type
/// (primitives like `"int"`, arrays, unresolved). Used for
/// `FieldType` / `ReturnType` / `ParameterType` and `GetType` on
/// primitives/arrays.
pub fn make_type_from_name(ctx: &VmContext, name: &str) -> Value {
    // Main module's own types first: the user program's classes live in the
    // main module's `type_registry`; the lazy loader below only covers
    // zpkg / stdlib types. (make-typeof-return-type — lets `typeof(UserClass)`
    // resolve to a real handle.)
    if let Some(m) = ctx.module() {
        if let Some(td) = m.type_registry.get(name) {
            return make_type_object(ctx, td.clone());
        }
    }
    if let Some(td) = ctx.try_lookup_type(name) {
        return make_type_object(ctx, td);
    }
    // Primitive / unresolved: present a canonical user-facing name. The VM uses
    // two tag vocabularies — field slots carry `"int"`/`"long"`, function
    // signatures carry `"i32"`/`"i64"`/`"str"` — so reflection normalizes both
    // to the C#-style aliases for a consistent surface.
    let canon = canonical_type_name(name);
    let simple = canon.rsplit('.').next().unwrap_or(&canon).to_string();
    build_type(ctx, &simple, &canon, NativeData::None)
}

/// `__typeof(name) -> Std.Type` — backs `typeof(T)` (make-typeof-return-type,
/// C2). The compiler emits the reflected type's fully-qualified name; this
/// resolves it to a `Std.Type` (real handle when the type is loaded —
/// user-program classes via the main module, stdlib via the lazy loader —
/// else a name-only synthetic Type for primitives / arrays / unbound generics).
pub fn builtin_typeof(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Str(s)) => Ok(make_type_from_name(ctx, s)),
        // Compiler always emits a string arg; be lenient otherwise.
        _ => Ok(Value::Null),
    }
}

/// Normalize a VM primitive type tag to its C#-style alias. User/class names
/// (anything not a known primitive tag) pass through unchanged.
fn canonical_type_name(tag: &str) -> String {
    match tag {
        "i8" => "sbyte",
        "u8" => "byte",
        "i16" => "short",
        "u16" => "ushort",
        "i32" => "int",
        "u32" => "uint",
        "i64" => "long",
        "u64" => "ulong",
        "f32" => "float",
        "f64" => "double",
        "str" => "string",
        other => other,
    }
    .to_string()
}

/// Allocate a `Std.Type` ScriptObject, writing `__name` / `__fullName` slots by
/// `field_index` and attaching `native`. Uses the real `Std.Type` TypeDesc so
/// the object responds to reflection methods.
fn build_type(ctx: &VmContext, simple: &str, full: &str, native: NativeData) -> Value {
    match ctx.try_lookup_type(well_known_names::STD_TYPE) {
        Some(type_td) => {
            let mut slots = vec![Value::Null; type_td.fields.len()];
            // align-type-memberinfo-hierarchy: `Name` is inherited from
            // `Std.Reflection.MemberInfo` (Type's base) — populate that slot so
            // `typeof(C).Name` / `(MemberInfo)typeof(C)).Name` resolve via the
            // shared base field (same as FieldInfo/MethodInfo). `__name` retained
            // for low-level golden / z42.test direct reads.
            if let Some(&i) = type_td.field_index.get("Name") {
                slots[i] = Value::Str(simple.to_string().into());
            }
            if let Some(&i) = type_td.field_index.get("__name") {
                slots[i] = Value::Str(simple.to_string().into());
            }
            if let Some(&i) = type_td.field_index.get("__fullName") {
                slots[i] = Value::Str(full.to_string().into());
            }
            ctx.heap().alloc_object(type_td, slots, native)
        }
        // z42.core not loaded — return a bare null Type (degraded). Reflection
        // is meaningless without z42.core, so this path is effectively dead.
        None => Value::Null,
    }
}

// ── Helpers ─────────────────────────────────────────────────────────────────

/// Pull the real `Arc<TypeDesc>` out of a `Std.Type`'s `NativeData::TypeHandle`.
/// Returns `None` for synthetic Types (primitives/arrays) so callers degrade
/// to empty results.
fn type_handle(args: &[Value]) -> Option<Arc<TypeDesc>> {
    match args.first() {
        Some(Value::Object(rc)) => {
            let obj = rc.borrow();
            match &obj.native {
                NativeData::TypeHandle(td) => Some(td.clone()),
                _ => None,
            }
        }
        _ => None,
    }
}

/// Allocate a z42 class instance (`type_name`), writing the given named slots by
/// `field_index`. Unlisted slots stay `Null`. Errors only if the class isn't
/// loaded (a hard environment bug, not user-reachable).
fn alloc_named(ctx: &VmContext, type_name: &str, named: &[(&str, Value)]) -> Result<Value> {
    let td = ctx
        .try_lookup_type(type_name)
        .ok_or_else(|| anyhow::anyhow!("reflection: {type_name} not loaded (z42.core missing?)"))?;
    let mut slots = vec![Value::Null; td.fields.len()];
    for (k, v) in named {
        if let Some(&i) = td.field_index.get(*k) {
            slots[i] = v.clone();
        }
    }
    Ok(ctx.heap().alloc_object(td, slots, NativeData::None))
}

/// Derive a method's simple name from a qualified function name
/// (`"Demo.Point.Foo"` → `"Foo"`).
fn simple_method_name(qualified: &str) -> &str {
    qualified.rsplit('.').next().unwrap_or(qualified)
}

/// Read a string slot from a `Std.Type` object by `field_index`. Backs the
/// `Name` / `FullName` extern properties (both handle-carrying and synthetic
/// Types have these slots written by `build_type`).
fn read_type_str_slot(args: &[Value], field: &str) -> Value {
    if let Some(Value::Object(rc)) = args.first() {
        // `type_desc()` is the lockless accessor on the GcRef; slots come from
        // the locked guard.
        if let Some(i) = rc.type_desc().field_index.get(field).copied() {
            let obj = rc.borrow();
            return obj.slots.get(i).cloned().unwrap_or(Value::Null);
        }
    }
    Value::Null
}

// align-type-memberinfo-hierarchy (2026-06-11): `__type_name` / `builtin_type_name`
// removed — `Type.Name` now resolves to the inherited `Std.Reflection.MemberInfo`
// `Name` field (populated by `build_type`), no native getter.

/// `__type_full_name(typeObj) -> string` — fully-qualified name (`Type.FullName`).
pub fn builtin_type_full_name(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    Ok(read_type_str_slot(args, "__fullName"))
}

// ── Field reflection ────────────────────────────────────────────────────────

/// `__type_fields(typeObj) -> FieldInfo[]` — instance fields (incl. inherited;
/// base-first), each `FieldInfo { Name, FieldType: Type }`.
pub fn builtin_type_fields(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let td = match type_handle(args) {
        Some(t) => t,
        None => return Ok(ctx.heap().alloc_array(Vec::new())),
    };
    let mut out = Vec::with_capacity(td.fields.len() + td.static_fields().len());
    // Instance fields (already base-first — cross-zpkg fixup merges inherited
    // instance fields into `td.fields`; IsStatic = false).
    for f in &td.fields {
        out.push(build_field_info(ctx, &td.name, &f.name, &f.type_tag, false)?);
    }
    // add-reflection-inherited-static-fields: static fields are stored per
    // declaring class (no instance-field-style fixup), so walk the base chain
    // and aggregate each ancestor's static fields (most-derived first), matching
    // C# `GetFields()` which includes inherited public statics. Each FieldInfo's
    // `__qualified` uses the DECLARING class name so attribute resolution targets
    // the right class. Dedup by name (a derived `new`-style shadow wins).
    let mut seen = std::collections::HashSet::new();
    let mut cur = Some(td.clone());
    while let Some(c) = cur {
        for f in c.static_fields() {
            if seen.insert(f.name.clone()) {
                out.push(build_field_info(ctx, &c.name, &f.name, &f.type_tag, true)?);
            }
        }
        cur = c.base_name.as_ref().and_then(|b| {
            ctx.module()
                .and_then(|m| m.type_registry.get(b).cloned())
                .or_else(|| ctx.try_lookup_type(b))
        });
    }
    Ok(ctx.heap().alloc_array(out))
}

/// Build a `FieldInfo`. `__qualified` ("<Class>.<Field>") lets
/// `FieldInfo.GetCustomAttributes()` resolve the field's attribute factories
/// (add-field-attribute-reflection).
fn build_field_info(
    ctx: &VmContext,
    class: &str,
    field: &str,
    type_tag: &str,
    is_static: bool,
) -> Result<Value> {
    let ftype = make_type_from_name(ctx, type_tag);
    alloc_named(
        ctx,
        STD_REFLECTION_FIELDINFO,
        &[
            ("Name", Value::Str(field.to_string().into())),
            ("FieldType", ftype),
            ("IsStatic", Value::Bool(is_static)),
            ("__qualified", Value::Str(format!("{class}.{field}").into())),
        ],
    )
}

// ── Attribute reflection (C3 add-attribute-reflection) ──────────────────────

/// `__type_custom_attributes(typeObj) -> Std.Attribute[]` — live attribute
/// instances for this type's user attributes, in application order.
///
/// Each attribute is built by invoking its compiler-synthesized factory
/// `() => new T(args)` (a normal z42 function) via `run_returning`. Attribute
/// construction is thus fully statically known (known class, known constructor,
/// constant args baked into the factory body) — no runtime `Activator`/`Invoke`
/// and no generic instantiation. Re-entering the interpreter here is safe:
/// `exec_function` keeps all per-call state in a stack-local `Frame`.
///
/// z42-level `Type.GetCustomAttributes()` caches the returned array, so repeated
/// calls on the same Type yield the same instances. Empty array for a
/// handle-less Type or a type with no attributes.
pub fn builtin_type_custom_attributes(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match type_handle(args) {
        Some(td) => call_attribute_factories(ctx, td.custom_attributes()),
        None => Ok(ctx.heap().alloc_array(Vec::new())),
    }
}

/// `__method_custom_attributes(qualified) -> Std.Attribute[]` — live attribute
/// instances for the method with the given qualified function name. C3b: the
/// z42 `MethodInfo` passes its hidden `__qualified` name; the builtin resolves
/// the backing `Function` (main module first, then lazy loader) and calls each
/// of its attribute factories. Same factory-call mechanism as the class path.
pub fn builtin_method_custom_attributes(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    // Args are [receiver MethodInfo, qualified: Str]; pick the string argument.
    let qualified = match args.iter().find_map(|v| match v {
        Value::Str(s) => Some(s.to_string()),
        _ => None,
    }) {
        Some(q) => q,
        None => return Ok(ctx.heap().alloc_array(Vec::new())),
    };
    let attrs: Vec<crate::metadata::bytecode::AttributeRef> = ctx
        .module()
        .and_then(|m| {
            m.func_index
                .get(qualified.as_str())
                .and_then(|&i| m.functions.get(i))
                .map(|f| f.custom_attributes().to_vec())
        })
        .or_else(|| ctx.try_lookup_function(&qualified).map(|f| f.custom_attributes().to_vec()))
        .unwrap_or_default();
    call_attribute_factories(ctx, &attrs)
}

/// `__field_custom_attributes(qualified) -> Std.Attribute[]` — live attribute
/// instances for the field named by "<Class>.<Field>" (FieldInfo passes its
/// hidden `__qualified`). Resolves the class TypeDesc, finds the field in
/// `cold.field_attributes`, and calls each factory. add-field-attribute-reflection.
pub fn builtin_field_custom_attributes(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let qualified = match args.iter().find_map(|v| match v {
        Value::Str(s) => Some(s.to_string()),
        _ => None,
    }) {
        Some(q) => q,
        None => return Ok(ctx.heap().alloc_array(Vec::new())),
    };
    // Split "<Class>.<Field>" at the last dot.
    let dot = match qualified.rfind('.') {
        Some(d) => d,
        None => return Ok(ctx.heap().alloc_array(Vec::new())),
    };
    let (class, field) = (&qualified[..dot], &qualified[dot + 1..]);
    let td = ctx
        .module()
        .and_then(|m| m.type_registry.get(class).cloned())
        .or_else(|| ctx.try_lookup_type(class));
    let attrs: Vec<crate::metadata::bytecode::AttributeRef> = td
        .as_ref()
        .map(|td| {
            td.field_attributes()
                .iter()
                .find(|(n, _)| n.as_ref() == field)
                .map(|(_, refs)| refs.to_vec())
                .unwrap_or_default()
        })
        .unwrap_or_default();
    call_attribute_factories(ctx, &attrs)
}

/// `__param_custom_attributes(qualified, position) -> Std.Attribute[]` — live
/// attribute instances for parameter `position` (source index, excluding the
/// implicit `this`) of the method/function named by `qualified`. The z42
/// `ParameterInfo` passes its hidden `__qualified` + `__position`. The backing
/// `Function`'s `param_attributes` are SIGS-aligned (include the `this` slot),
/// so the wire index = position + (is_static ? 0 : 1). add-parameter-attribute-reflection.
pub fn builtin_param_custom_attributes(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let qualified = match args.iter().find_map(|v| match v {
        Value::Str(s) => Some(s.to_string()),
        _ => None,
    }) {
        Some(q) => q,
        None => return Ok(ctx.heap().alloc_array(Vec::new())),
    };
    let position = match args.iter().find_map(|v| match v {
        Value::I64(n) if *n >= 0 => Some(*n as usize),
        _ => None,
    }) {
        Some(p) => p,
        None => return Ok(ctx.heap().alloc_array(Vec::new())),
    };
    // Resolve the backing Function (main module first, then lazy loader) and read
    // its SIGS-aligned per-param attrs. wire_index = position + (this offset).
    let lookup = |f: &crate::metadata::bytecode::Function| {
        let wire_index = position + if f.is_static { 0 } else { 1 };
        f.param_attributes().get(wire_index).map(|a| a.to_vec())
    };
    let attrs: Vec<crate::metadata::bytecode::AttributeRef> = ctx
        .module()
        .and_then(|m| {
            m.func_index
                .get(qualified.as_str())
                .and_then(|&i| m.functions.get(i))
                .and_then(lookup)
        })
        .or_else(|| ctx.try_lookup_function(&qualified).and_then(|f| lookup(&f)))
        .unwrap_or_default();
    call_attribute_factories(ctx, &attrs)
}

/// Build live attribute instances by invoking each synthesized factory function
/// (`() => new T(args)`) via `run_returning`. Shared by the class
/// (`__type_custom_attributes`) and method (`__method_custom_attributes`) paths.
/// Cross-zpkg factories resolve via the lazy loader. Re-entering the interpreter
/// here is safe — `exec_function` keeps per-call state in a stack-local `Frame`.
fn call_attribute_factories(
    ctx: &VmContext,
    attrs: &[crate::metadata::bytecode::AttributeRef],
) -> Result<Value> {
    if attrs.is_empty() {
        return Ok(ctx.heap().alloc_array(Vec::new()));
    }
    let module = match ctx.module() {
        Some(m) => m.clone(),
        None => return Ok(ctx.heap().alloc_array(Vec::new())),
    };
    let mut out = Vec::with_capacity(attrs.len());
    for a in attrs {
        let instance = if let Some(&idx) = module.func_index.get(a.factory_func.as_str()) {
            crate::interp::run_returning(ctx, &module, &module.functions[idx], &[])?
        } else if let Some(func) = ctx.try_lookup_function(&a.factory_func) {
            crate::interp::run_returning(ctx, &module, func.as_ref(), &[])?
        } else {
            None
        };
        out.push(instance.unwrap_or(Value::Null));
    }
    Ok(ctx.heap().alloc_array(out))
}

// ── Method reflection ───────────────────────────────────────────────────────

/// `__type_methods(typeObj) -> MethodInfo[]` — vtable (virtual/inherited) plus
/// declared non-virtual methods, deduped by qualified name.
pub fn builtin_type_methods(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let td = match type_handle(args) {
        Some(t) => t,
        None => return Ok(ctx.heap().alloc_array(Vec::new())),
    };
    let mut seen: HashSet<String> = HashSet::new();
    let mut out = Vec::new();
    // Virtual / inherited methods carry their simple name in the vtable.
    for (simple, qualified) in &td.vtable {
        if seen.insert(qualified.clone()) {
            out.push(build_method_info(ctx, simple, qualified, true)?);
        }
    }
    // Declared non-virtual methods (qualified names only).
    for qualified in td.own_methods() {
        let q = qualified.to_string();
        if seen.insert(q.clone()) {
            let simple = simple_method_name(&q).to_string();
            out.push(build_method_info(ctx, &simple, &q, false)?);
        }
    }
    Ok(ctx.heap().alloc_array(out))
}

/// Build a `MethodInfo` by resolving the backing `Function` for its signature.
/// Missing Function (extern/native or unresolved) → name-only MethodInfo.
fn build_method_info(
    ctx: &VmContext,
    simple: &str,
    qualified: &str,
    is_virtual: bool,
) -> Result<Value> {
    let (ret_tag, is_static, params) = match resolve_func_sig(ctx, qualified) {
        Some((param_count, ret_type, fn_is_static, param_types)) => {
            // Instance methods carry `this` at param 0 — skip it.
            let start = if fn_is_static { 0 } else { 1 };
            let mut params = Vec::new();
            for i in start..param_count {
                let tag = param_types.get(i).map(|s| s.as_str()).unwrap_or("?");
                let pos = (i - start) as i64;
                params.push(alloc_named(
                    ctx,
                    STD_REFLECTION_PARAMINFO,
                    &[
                        ("Name", Value::Str(format!("arg{pos}").into())),
                        ("ParameterType", make_type_from_name(ctx, tag)),
                        ("Position", Value::I64(pos)),
                        // add-parameter-attribute-reflection: backing func name so
                        // ParameterInfo.GetCustomAttributes() can resolve the param's
                        // attribute factories (paired with Position).
                        ("__qualified", Value::Str(qualified.to_string().into())),
                    ],
                )?);
            }
            (ret_type, fn_is_static, params)
        }
        None => ("void".to_string(), false, Vec::new()),
    };
    let params_arr = ctx.heap().alloc_array(params);
    alloc_named(
        ctx,
        STD_REFLECTION_METHODINFO,
        &[
            ("Name", Value::Str(simple.to_string().into())),
            ("ReturnType", make_type_from_name(ctx, &ret_tag)),
            ("IsStatic", Value::Bool(is_static)),
            ("IsVirtual", Value::Bool(is_virtual)),
            ("__parameters", params_arr),
            // C3b: qualified func name so MethodInfo.GetCustomAttributes() can
            // resolve the backing Function's attribute factories.
            ("__qualified", Value::Str(qualified.to_string().into())),
        ],
    )
}

/// Resolve a function's signature `(param_count, ret_type, is_static, param_types)`
/// by qualified name. Checks the **main module**'s `func_index` first (the
/// user program's own methods), then the lazy loader (stdlib / zpkg methods).
/// `try_lookup_function` alone misses main-module functions.
fn resolve_func_sig(
    ctx: &VmContext,
    qualified: &str,
) -> Option<(usize, String, bool, Vec<String>)> {
    if let Some(m) = ctx.module() {
        if let Some(&i) = m.func_index.get(qualified) {
            if let Some(f) = m.functions.get(i) {
                return Some((
                    f.param_count,
                    f.ret_type.clone(),
                    f.is_static,
                    f.param_types().to_vec(),
                ));
            }
        }
    }
    ctx.try_lookup_function(qualified).map(|f| {
        (
            f.param_count,
            f.ret_type.clone(),
            f.is_static,
            f.param_types().to_vec(),
        )
    })
}

// ── Property reflection ─────────────────────────────────────────────────────

/// `__type_properties(typeObj) -> PropertyInfo[]` — properties derived from the
/// `get_<X>` / `set_<X>` accessor-method convention (auto-properties desugar to
/// field + get_/set_ methods). No persisted PropertyDesc metadata and no
/// wire-format change: the accessor names already live in the vtable /
/// own_methods, and the property type comes from the accessor signature (same
/// source as MethodInfo). Getter + setter for the same name merge into one
/// PropertyInfo (CanRead && CanWrite). Empty for a handle-less Type.
pub fn builtin_type_properties(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let td = match type_handle(args) {
        Some(t) => t,
        None => return Ok(ctx.heap().alloc_array(Vec::new())),
    };
    // vtable (virtual / inherited, base-first) then declared non-virtual methods
    // — same ordering + dedup as GetMethods, so property order is stable.
    let mut props: Vec<PropAccum> = Vec::new();
    let mut seen: HashSet<String> = HashSet::new();
    for (simple, qualified) in &td.vtable {
        if seen.insert(qualified.clone()) {
            accumulate_property(ctx, simple, qualified, &mut props);
        }
    }
    for qualified in td.own_methods() {
        let q = qualified.to_string();
        if seen.insert(q.clone()) {
            let simple = simple_method_name(&q).to_string();
            accumulate_property(ctx, &simple, &q, &mut props);
        }
    }
    let mut out = Vec::with_capacity(props.len());
    for p in &props {
        let type_tag = p
            .getter_type
            .as_deref()
            .or(p.setter_type.as_deref())
            .unwrap_or("?");
        out.push(alloc_named(
            ctx,
            STD_REFLECTION_PROPERTYINFO,
            &[
                ("Name", Value::Str(p.name.clone().into())),
                ("PropertyType", make_type_from_name(ctx, type_tag)),
                ("CanRead", Value::Bool(p.getter_type.is_some())),
                ("CanWrite", Value::Bool(p.setter_type.is_some())),
            ],
        )?);
    }
    Ok(ctx.heap().alloc_array(out))
}

/// Accumulator for merging a property's getter + setter into one PropertyInfo.
struct PropAccum {
    name: String,
    getter_type: Option<String>,
    setter_type: Option<String>,
}

/// Classify one method as a property getter / setter (by `get_` / `set_` prefix)
/// and merge it into `props`. A resolvable accessor must have the right logical
/// arity (getter 0, setter 1, ignoring `this`) — otherwise it's a regular method
/// that merely shares the prefix and is skipped. Unresolvable signatures
/// (extern / native getters) are accepted leniently with a best-effort type.
fn accumulate_property(ctx: &VmContext, simple: &str, qualified: &str, props: &mut Vec<PropAccum>) {
    let (is_get, prop_name) = match (simple.strip_prefix("get_"), simple.strip_prefix("set_")) {
        (Some(n), _) => (true, n),
        (_, Some(n)) => (false, n),
        _ => return,
    };
    if prop_name.is_empty() {
        return;
    }
    let sig = resolve_func_sig(ctx, qualified);
    if is_get {
        let ty = match &sig {
            Some((pc, ret, is_static, _)) => {
                if pc.saturating_sub(if *is_static { 0 } else { 1 }) != 0 {
                    return; // get_X(args) — a regular method, not a property
                }
                ret.clone()
            }
            None => "?".to_string(),
        };
        upsert_prop(props, prop_name).getter_type = Some(ty);
    } else {
        let ty = match &sig {
            Some((pc, _, is_static, ptypes)) => {
                let base = if *is_static { 0 } else { 1 };
                if pc.saturating_sub(base) != 1 {
                    return; // set_X with != 1 value param — a regular method
                }
                ptypes.get(base).cloned().unwrap_or_else(|| "?".to_string())
            }
            None => "?".to_string(),
        };
        upsert_prop(props, prop_name).setter_type = Some(ty);
    }
}

/// Find-or-insert a property accumulator by name, preserving first-seen order.
fn upsert_prop<'a>(props: &'a mut Vec<PropAccum>, name: &str) -> &'a mut PropAccum {
    if let Some(i) = props.iter().position(|p| p.name == name) {
        return &mut props[i];
    }
    props.push(PropAccum {
        name: name.to_string(),
        getter_type: None,
        setter_type: None,
    });
    let last = props.len() - 1;
    &mut props[last]
}

// ── Base type & generic arguments ───────────────────────────────────────────

/// `__type_base(typeObj) -> Type | null` — base class Type; `Std.Object` for
/// classes with no explicit base; `null` for `Std.Object` itself / no handle.
pub fn builtin_type_base(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let td = match type_handle(args) {
        Some(t) => t,
        None => return Ok(Value::Null),
    };
    match &td.base_name {
        Some(b) => Ok(make_type_from_name(ctx, b)),
        None => {
            if td.name == STD_OBJECT {
                Ok(Value::Null)
            } else {
                Ok(make_type_from_name(ctx, STD_OBJECT))
            }
        }
    }
}

/// `__type_generic_args(typeObj) -> Type[]` — instantiated generic type args
/// (`Box<int>` → `[typeof(int)]`); empty for non-generic / open types.
pub fn builtin_type_generic_args(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let td = match type_handle(args) {
        Some(t) => t,
        None => return Ok(ctx.heap().alloc_array(Vec::new())),
    };
    let out: Vec<Value> = td
        .type_args()
        .iter()
        .map(|tag| make_type_from_name(ctx, tag))
        .collect();
    Ok(ctx.heap().alloc_array(out))
}

/// `__type_members(typeObj) -> MemberInfo[]` — fields then methods. Built in
/// Rust to sidestep z42 array covariance (the mixed array holds FieldInfo +
/// MethodInfo, both `MemberInfo` subclasses).
pub fn builtin_type_members(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let mut out = Vec::new();
    if let Value::Array(a) = builtin_type_fields(ctx, args)? {
        out.extend(a.borrow().iter().cloned());
    }
    if let Value::Array(m) = builtin_type_methods(ctx, args)? {
        out.extend(m.borrow().iter().cloned());
    }
    Ok(ctx.heap().alloc_array(out))
}

// ── Type flags (add-reflection-type-flags, zbc 1.12) ────────────────────────

/// `__type_is_abstract(typeObj) -> bool` — true if the class was declared
/// `abstract`. False for handle-less Types (primitives / arrays).
pub fn builtin_type_is_abstract(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    Ok(Value::Bool(class_flag_set(
        args,
        crate::metadata::bytecode::CLASS_FLAG_ABSTRACT,
    )))
}

/// `__type_is_sealed(typeObj) -> bool` — true if the class was declared
/// `sealed`. False for handle-less Types.
pub fn builtin_type_is_sealed(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    Ok(Value::Bool(class_flag_set(
        args,
        crate::metadata::bytecode::CLASS_FLAG_SEALED,
    )))
}

/// `__type_is_value_type(typeObj) -> bool` — true for a `struct` (value type).
/// Reads the struct bit captured in the TYPE-section flags byte (no new wire).
/// add-reflection-value-record-flags.
pub fn builtin_type_is_value_type(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    Ok(Value::Bool(class_flag_set(
        args,
        crate::metadata::bytecode::CLASS_FLAG_STRUCT,
    )))
}

/// `__type_is_record(typeObj) -> bool` — true if the type was declared `record`.
pub fn builtin_type_is_record(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    Ok(Value::Bool(class_flag_set(
        args,
        crate::metadata::bytecode::CLASS_FLAG_RECORD,
    )))
}

/// Is the given class-flag bit set on the reflected Type's `TypeDesc`?
/// Handle-less Types (primitive / array, `NativeData::None`) → false (lenient).
fn class_flag_set(args: &[Value], bit: u8) -> bool {
    type_handle(args)
        .map(|td| td.class_flags & bit != 0)
        .unwrap_or(false)
}

#[cfg(test)]
#[path = "reflection_tests.rs"]
mod reflection_tests;
