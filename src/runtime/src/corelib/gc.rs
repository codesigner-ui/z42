//! GC control builtins exposed to z42 scripts.
//!
//! Wired to `Std.GC.*` static class declared in `src/libraries/z42.core/src/GC.z42`.
//! Each method has `[Native("__gc_*")]` attribute mapping to the dispatch entries
//! in `super::dispatch_table()`.
//!
//! 2026-04-29 expose-gc-to-scripts (Phase 3d.2): 让脚本能显式触发 cycle
//! collection 与查询堆使用量，用于端到端验证 GC 与 host 集成应用。

use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::Result;

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
