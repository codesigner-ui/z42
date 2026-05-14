//! GC control builtins exposed to z42 scripts.
//!
//! Wired to `Std.GC.*` static class declared in `src/libraries/z42.core/src/GC.z42`.
//! Each method has `[Native("__gc_*")]` attribute mapping to the dispatch entries
//! in `super::dispatch_table()`.
//!
//! 2026-04-29 expose-gc-to-scripts (Phase 3d.2): 让脚本能显式触发 cycle
//! collection 与查询堆使用量，用于端到端验证 GC 与 host 集成应用。

use crate::gc::GcHandleKind;
use crate::metadata::{FieldSlot, NativeData, TypeDesc, Value};
use crate::vm_context::VmContext;
use anyhow::{anyhow, Result};
use std::collections::HashMap;
use std::sync::{Arc, OnceLock};

/// `Std.GC.Collect()` —— 触发环检测（不阻断；no-op 当 paused）。
/// 调用 `ctx.heap().collect_cycles()`，进而走 `RcMagrGC` 的 trial-deletion 算法。
pub fn builtin_gc_collect(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    ctx.heap().collect_cycles();
    Ok(Value::Null)
}

/// `Std.GC.UsedBytes()` —— 返回当前 `HeapStats.used_bytes`（i64）。
/// RC 模式 + cycle collector：alloc 累加；cycle collect 后按 freed_bytes 减去。
pub fn builtin_gc_used_bytes(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    Ok(Value::I64(ctx.heap().used_bytes() as i64))
}

/// `Std.GC.ForceCollect()` —— 强制完整 collect，返回 `freed_bytes`（i64）。
/// 与 `Collect()` 区别：前者是建议性（pause 时跳过），后者总是触发并返回数据。
pub fn builtin_gc_force_collect(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let stats = ctx.heap().force_collect();
    Ok(Value::I64(stats.freed_bytes as i64))
}

// ── reorganize-gc-stdlib (2026-05-07) ────────────────────────────────────────
//
// `Std.GCHandle` (struct, single `_slot: long`) and `Std.HeapStats` (class,
// 7 long fields) builtins. The handle slot itself lives in the GC layer; the
// builtins below are thin shims that project Rust enums / structs into z42
// `Value::Object`s with the corresponding TypeDesc. See:
// - `src/libraries/z42.core/src/GC/GCHandle.z42`
// - `src/libraries/z42.core/src/GC/HeapStats.z42`
// - `src/runtime/src/gc/heap.rs` HandleTable trait API

/// `Std.GCHandleType.Weak` enum value (corresponds to z42 `enum` first variant
/// being numbered 0). `GCHandleType.Strong` is 1.
const GC_HANDLE_TYPE_WEAK: i64   = 0;
const GC_HANDLE_TYPE_STRONG: i64 = 1;

fn gc_handle_kind_from_i64(v: i64) -> GcHandleKind {
    match v {
        GC_HANDLE_TYPE_WEAK => GcHandleKind::Weak,
        _                   => GcHandleKind::Strong,
    }
}

fn gc_handle_kind_to_i64(k: GcHandleKind) -> i64 {
    match k {
        GcHandleKind::Weak   => GC_HANDLE_TYPE_WEAK,
        GcHandleKind::Strong => GC_HANDLE_TYPE_STRONG,
    }
}

/// `Std.GCHandle` TypeDesc — single `_slot: long` field. Cached so that
/// repeated `Alloc` calls share a single Arc.
fn gc_handle_type_desc() -> Arc<TypeDesc> {
    static CACHE: OnceLock<Arc<TypeDesc>> = OnceLock::new();
    CACHE.get_or_init(|| {
        let mut field_index = HashMap::new();
        field_index.insert("_slot".to_string(), 0usize);
        let fields = vec![FieldSlot { name: "_slot".to_string(), type_tag: "long".to_string() }];
        Arc::new(TypeDesc {
            name: "Std.GCHandle".to_string(),
            base_name: None,
            own_fields: fields.clone(),
            own_methods: vec![],
            fields,
            field_index,
            vtable: Vec::new(),
            vtable_index: HashMap::new(),
            type_params: vec![],
            type_args: vec![],
            type_param_constraints: vec![],
            id: crate::metadata::tokens::TypeId::UNRESOLVED,
        })
    }).clone()
}

/// `Std.HeapStats` TypeDesc — 7 long fields projecting Rust `HeapStats`.
///
/// The script declares each as an auto-property `public long X { get; }`, which
/// the compiler desugars to a private backing field `__prop_X` + `get_X()`
/// accessor. So slot names here carry the `__prop_` prefix.
fn heap_stats_type_desc() -> Arc<TypeDesc> {
    static CACHE: OnceLock<Arc<TypeDesc>> = OnceLock::new();
    CACHE.get_or_init(|| {
        let names = [
            "__prop_Allocations", "__prop_GcCycles", "__prop_UsedBytes", "__prop_MaxBytes",
            "__prop_RootsPinned", "__prop_FinalizersPending", "__prop_Observers",
        ];
        let mut field_index = HashMap::new();
        let mut fields = Vec::with_capacity(names.len());
        for (i, n) in names.iter().enumerate() {
            field_index.insert(n.to_string(), i);
            fields.push(FieldSlot { name: n.to_string(), type_tag: "long".to_string() });
        }
        Arc::new(TypeDesc {
            name: "Std.HeapStats".to_string(),
            base_name: None,
            own_fields: fields.clone(),
            own_methods: vec![],
            fields,
            field_index,
            vtable: Vec::new(),
            vtable_index: HashMap::new(),
            type_params: vec![],
            type_args: vec![],
            type_param_constraints: vec![],
            id: crate::metadata::tokens::TypeId::UNRESOLVED,
        })
    }).clone()
}

/// Read the `_slot: long` field out of a `Std.GCHandle` receiver. Treats null /
/// non-Object / missing field as slot 0 ("unallocated").
fn extract_gc_handle_slot(arg: &Value) -> u64 {
    let Value::Object(rc) = arg else { return 0 };
    let obj = rc.borrow();
    match obj.slots.first() {
        Some(Value::I64(i)) => (*i).max(0) as u64,
        _ => 0,
    }
}

fn make_gc_handle(ctx: &VmContext, slot: u64) -> Value {
    ctx.heap().alloc_object(
        gc_handle_type_desc(),
        vec![Value::I64(slot as i64)],
        NativeData::None,
    )
}

/// `Std.GCHandle.Alloc(target, GCHandleType type)` — returns a new GCHandle
/// struct. Atomic-Weak / null target → `_slot=0` (`IsAllocated=false`).
pub fn builtin_gc_handle_alloc(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let target = args.first().ok_or_else(|| anyhow!("__gc_handle_alloc: missing target arg"))?;
    let kind_int = match args.get(1) {
        Some(Value::I64(v)) => *v,
        _ => return Err(anyhow!("__gc_handle_alloc: expected GCHandleType (int) arg")),
    };
    let slot = ctx.heap().handle_alloc(target, gc_handle_kind_from_i64(kind_int));
    Ok(make_gc_handle(ctx, slot))
}

/// `Std.GCHandle.Target { get; }` — returns null when slot freed or weak target collected.
pub fn builtin_gc_handle_target(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = extract_gc_handle_slot(args.first().unwrap_or(&Value::Null));
    Ok(ctx.heap().handle_target(slot).unwrap_or(Value::Null))
}

/// `Std.GCHandle.IsAllocated { get; }`.
pub fn builtin_gc_handle_is_alloc(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = extract_gc_handle_slot(args.first().unwrap_or(&Value::Null));
    Ok(Value::Bool(ctx.heap().handle_is_alloc(slot)))
}

/// `Std.GCHandle.Kind { get; }` — returns `GCHandleType` enum int. Freed slot
/// returns `Strong` (default) — callers should check `IsAllocated` first.
pub fn builtin_gc_handle_kind(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = extract_gc_handle_slot(args.first().unwrap_or(&Value::Null));
    let kind = ctx.heap().handle_kind(slot).unwrap_or(GcHandleKind::Strong);
    Ok(Value::I64(gc_handle_kind_to_i64(kind)))
}

/// `Std.GCHandle.Free()` — releases the slot. Idempotent (also no-op on slot 0).
pub fn builtin_gc_handle_free(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = extract_gc_handle_slot(args.first().unwrap_or(&Value::Null));
    ctx.heap().handle_free(slot);
    Ok(Value::Null)
}

/// `Std.GC.GetStats()` — projects Rust `HeapStats` into a `Std.HeapStats` instance.
/// `MaxBytes` uses `-1` as the unlimited sentinel (z42 has no `Optional<T>`).
pub fn builtin_gc_stats(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let s = ctx.heap().stats();
    Ok(ctx.heap().alloc_object(
        heap_stats_type_desc(),
        vec![
            Value::I64(s.allocations        as i64),
            Value::I64(s.gc_cycles          as i64),
            Value::I64(s.used_bytes         as i64),
            Value::I64(s.max_bytes.map(|v| v as i64).unwrap_or(-1)),
            Value::I64(s.roots_pinned       as i64),
            Value::I64(s.finalizers_pending as i64),
            Value::I64(s.observers          as i64),
        ],
        NativeData::None,
    ))
}

