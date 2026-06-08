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

/// `__type_name(typeObj) -> string` — unqualified name (`Type.Name`).
pub fn builtin_type_name(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    Ok(read_type_str_slot(args, "__name"))
}

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
    let mut out = Vec::with_capacity(td.fields.len());
    for f in &td.fields {
        let ftype = make_type_from_name(ctx, &f.type_tag);
        out.push(alloc_named(
            ctx,
            STD_REFLECTION_FIELDINFO,
            &[
                ("Name", Value::Str(f.name.to_string().into())),
                ("FieldType", ftype),
            ],
        )?);
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

#[cfg(test)]
#[path = "reflection_tests.rs"]
mod reflection_tests;
