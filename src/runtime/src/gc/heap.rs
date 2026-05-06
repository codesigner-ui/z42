//! `MagrGC` —— z42 VM 的 GC 抽象接口（嵌入式宿主友好版）。
//!
//! 命名取自《银河系漫游指南》中的 **Magrathea** —— 那颗专门建造定制行星的传奇
//! 世界。
//!
//! # 设计
//!
//! trait 形状对齐 [MMTk](https://www.mmtk.io/) 的 `VMBinding` porting contract
//! —— OpenJDK / V8 / Julia / Ruby / RustPython 的事实标准 GC 抽象。把
//! MMTk 拆分的 sub-trait（`ObjectModel` / `Scanning` / `Collection` /
//! `ReferenceGlue` / ...）合到一个 trait 里按"能力组"分段，让 z42 体量下
//! 接口更易读，未来如需拆 sub-trait 切割面清晰。
//!
//! # 能力组
//!
//! 1. **Allocation** —— 堆分配入口
//! 2. **Roots** —— host-side 显式 pin/unpin + frame scope + GC-side scan
//! 3. **Write barriers** —— 字段 / 数组元素写屏障（默认 no-op；generational
//!    / 自定义堆 / MMTk 集成等后续迭代会重载，见 vm-architecture.md "GC 后续迭代规划" A3 / D1）
//! 4. **Object Model** —— 对象尺寸 / 引用扫描 helper（用于 trace / snapshot）
//! 5. **Collection control** —— collect / cycles / force / pause / resume
//! 6. **Heap config** —— max_bytes / used_bytes
//! 7. **Finalization** —— register / cancel finalizer
//! 8. **Weak references** —— make / upgrade weak
//! 9. **Event observers** —— add / remove observer + GcEvent
//! 10. **Profiler** —— alloc sampler / heap snapshot / iterate live
//! 11. **Stats** —— HeapStats 快照
//!
//! # Phase 路线
//!
//! 见 [`docs/design/vm-architecture.md`](../../../../docs/design/vm-architecture.md)
//! "GC 子系统" 段。

use std::sync::Arc;

use crate::metadata::{NativeData, TypeDesc, Value};

pub use super::types::{
    AllocKind, AllocSample, AllocSamplerFn, CollectStats, FinalizerFn, FrameMark,
    GcEvent, GcHandleKind, GcKind, GcObserver, HeapSnapshot, HeapStats, ObjectStats,
    ObserverId, RootHandle, SnapshotCoverage, WeakRef,
};

/// MagrGC —— z42 VM 的 GC 抽象接口（host-friendly）。
///
/// # 实现契约
///
/// - `alloc_*` 返回的 `Value` 与对应 variant 一致（Object / Array）
/// - 多次 `alloc_*` 返回的堆对象互相独立（`Rc::ptr_eq` 为 false）
/// - `pin_root` 返回的 `RootHandle` 在该 heap 内唯一，且 `unpin_root(h)` 后该
///   handle 不再有效
/// - `for_each_root` 必须遍历**所有当前活跃**的 pinned root（含 frame 内）
/// - `enter_frame` 与 `leave_frame` 必须严格配对（栈式）
/// - 实现自负 `&self` 接口背后的内部可变性
pub trait MagrGC: std::fmt::Debug {
    // ── 1. Allocation ────────────────────────────────────────────────────────

    /// 分配一个 `ScriptObject` 并以 `Value::Object` 返回。
    fn alloc_object(
        &self,
        type_desc: Arc<TypeDesc>,
        slots: Vec<Value>,
        native: NativeData,
    ) -> Value;

    /// 分配一个 `Vec<Value>` 数组并以 `Value::Array` 返回。
    fn alloc_array(&self, elems: Vec<Value>) -> Value;

    // ── 2. Roots ─────────────────────────────────────────────────────────────

    /// 把一个 value 加入 root set，host 持有返回的 `RootHandle` 期间该值
    /// 不会被 GC 回收。等价于 V8 `Persistent<T>` / .NET `GCHandle.Alloc(Normal)`。
    fn pin_root(&self, value: Value) -> RootHandle;

    /// 释放 `pin_root` 返回的 handle。释放后的 handle 不应再次使用。
    fn unpin_root(&self, handle: RootHandle);

    /// 进入一个 root scope frame。同 frame 内所有 `pin_root` 在
    /// `leave_frame(mark)` 时自动 unpin（无需逐个调 `unpin_root`）。
    fn enter_frame(&self) -> FrameMark;

    /// 离开 frame，丢弃自 `enter_frame` 起 pin 的所有 root。
    fn leave_frame(&self, mark: FrameMark);

    /// 遍历当前所有活跃 root（`pin_root` + frame pins）。GC 实现 trace 时使用。
    fn for_each_root(&self, visitor: &mut dyn FnMut(&Value));

    // ── 3. Write barriers ────────────────────────────────────────────────────

    /// 写屏障：当 `owner` 对象的 `slot` 字段被赋为 `new` 时通知 GC。
    /// Phase 2+ 用于 generational / SATB；Phase 1 默认 no-op。
    fn write_barrier_field(&self, _owner: &Value, _slot: usize, _new: &Value) {}

    /// 数组元素写屏障；同 `write_barrier_field`。
    fn write_barrier_array_elem(&self, _arr: &Value, _idx: usize, _new: &Value) {}

    // ── 4. Object Model ──────────────────────────────────────────────────────

    /// 估计对象的浅尺寸（不递归 nested values）。Phase 1 实现给出 enum tag +
    /// 容器自身的合理估计；Phase 3 trace 时会被精确化。
    fn object_size_bytes(&self, value: &Value) -> usize;

    /// 访问 `value` 中所有引用类型的内嵌 Value。
    /// - `Value::Object` → 每个 slot
    /// - `Value::Array`  → 每个元素
    /// - 原子值（I64/F64/Bool/Char/Str/Null）→ 不调 visitor
    fn scan_object_refs(&self, value: &Value, visitor: &mut dyn FnMut(&Value));

    // ── 5. Collection control ────────────────────────────────────────────────

    /// 触发完整 GC（stop-the-world tracing）。Phase 1 默认 no-op。
    fn collect(&self) {}

    /// 触发环引用检测与回收。Phase 1 默认 no-op（仅递增 stats counter
    /// 与发 GcEvent）。
    fn collect_cycles(&self) {}

    /// 强制立即回收，返回本次 GC 的统计。返回 `kind: None` 表示被
    /// `pause()` 跳过。
    fn force_collect(&self) -> CollectStats;

    /// 暂停 GC（关键区使用，如 host 在采样热路径时不希望 GC 介入）。
    /// 暂停期间 `force_collect` / `collect_cycles` 跳过实际工作但仍返回
    /// 一致结果。
    fn pause(&self);

    /// 恢复 GC 工作。`pause` / `resume` 嵌套调用，需配对。
    fn resume(&self);

    // ── 6. Heap config ───────────────────────────────────────────────────────

    /// 设置堆字节上限（`None` = 不限制）。超过 75% 触发 `AllocationPressure`，
    /// 超过 90% 触发 `NearHeapLimit`，越界触发 `OutOfMemory`。
    /// 默认情况下（strict_oom=false）alloc 仍然成功，仅通知；启用
    /// `set_strict_oom(true)` 后 alloc 越界返回 `Value::Null` 不实际占用 heap。
    fn set_max_heap_bytes(&self, max: Option<u64>);

    /// 已用字节数（同 `stats().used_bytes`）。
    fn used_bytes(&self) -> u64;

    /// 启用 / 关闭 **strict OOM 模式**。Phase 3-OOM (2026-04-29)。
    /// 默认 false（行为兼容历史：alloc 越界仅 fire 事件不拒绝）。
    /// 启用后：`alloc_*` 越过 `max_heap_bytes` 时返回 `Value::Null`、不入 registry、
    /// 不 bump used_bytes，仍 fire `OutOfMemory` 事件让 observer 感知。
    /// 调用方（script）见到 Null 通常会在后续访问产生 NullReferenceException；
    /// host 可通过 OOM observer 提前感知并主动管理（kill VM / 重置 heap 等）。
    fn set_strict_oom(&self, _enabled: bool) {}

    // ── 7. Finalization ──────────────────────────────────────────────────────

    /// 注册一个 finalizer，当 `value` 不可达时触发。
    ///
    /// **Phase 1 RC 模式限制**：注册被记录（`stats().finalizers_pending` 加 1），
    /// 但 callback **不会被自动调用** —— `Rc<RefCell<T>>` Drop 不可拦截。
    /// Phase 3 mark-sweep 调度真实触发。
    fn register_finalizer(&self, value: &Value, finalizer: FinalizerFn);

    /// 取消之前注册的 finalizer。
    fn cancel_finalizer(&self, value: &Value);

    // ── 8. Weak references ───────────────────────────────────────────────────

    /// 创建对 `value` 的弱引用。原子值（I64/Str/Null/...）返回 `None`。
    fn make_weak(&self, value: &Value) -> Option<WeakRef>;

    /// 尝试从弱引用恢复强引用。若目标已被回收（无强引用持有）返回 `None`。
    fn upgrade_weak(&self, weak: &WeakRef) -> Option<Value>;

    // ── 8.5 Handle table（reorganize-gc-stdlib，2026-05-07）─────────────────
    //
    // Slab + free-list backed handle table that powers `Std.GCHandle` (struct,
    // single `_slot: long`). Slot 0 is reserved as the "unallocated" sentinel —
    // any caller seeing `_slot == 0` knows the handle was never bound to a real
    // entry (e.g. `AllocWeak` on an atomic value).
    //
    // Strong slots `Rc::clone` the underlying ScriptObject / Vec<Value>, which
    // anchors the target across GC collection. Weak slots store a `Weak<...>`
    // and return `None` from `handle_target` once the target has been dropped.
    // Both kinds require explicit `handle_free` to release the slot — copying a
    // GCHandle struct duplicates the slot ID, so freeing one alias frees the
    // backing for all aliases (matches C# `GCHandle` struct semantics).

    /// Allocate a handle table slot for `target` with the given `kind`.
    /// Returns the slot id (always `>= 1` on success, `0` for "could not
    /// allocate"). The current rejection conditions:
    /// - `kind == Weak` and `target` is an atomic value (no Rc backing) → 0
    /// - `target == Value::Null` → 0
    fn handle_alloc(&self, target: &Value, kind: GcHandleKind) -> u64;

    /// Read the current target of `slot`. Returns `None` when the slot has been
    /// freed, or — for Weak slots — when the target has been collected.
    fn handle_target(&self, slot: u64) -> Option<Value>;

    /// `true` until `handle_free(slot)` is called. **Note**: for Weak slots
    /// `is_alloc` stays `true` even after the target is collected (slot is
    /// still owned by its handle). Use `handle_target` to detect collection.
    fn handle_is_alloc(&self, slot: u64) -> bool;

    /// `Some(kind)` while the slot is allocated; `None` after `handle_free`.
    fn handle_kind(&self, slot: u64) -> Option<GcHandleKind>;

    /// Release `slot`. Idempotent: freeing a slot that has already been freed
    /// (or never allocated, e.g. slot 0) is a no-op.
    fn handle_free(&self, slot: u64);

    // ── 9. Event observers ───────────────────────────────────────────────────

    /// 注册一个 GC 事件观察者。
    fn add_observer(&self, observer: Arc<dyn GcObserver>) -> ObserverId;

    /// 移除一个观察者。
    fn remove_observer(&self, id: ObserverId);

    // ── 10. Profiler ─────────────────────────────────────────────────────────

    /// 安装分配采样器（每次 `alloc_*` 触发回调），传 `None` 卸载。
    fn set_alloc_sampler(&self, sampler: Option<AllocSamplerFn>);

    /// 拍下堆快照（按 type_desc.name 聚合）。
    ///
    /// **Phase 1 RC 模式**：snapshot 仅覆盖**从 pinned roots 可达**的对象，
    /// `coverage = SnapshotCoverage::ReachableFromPinnedRoots`。Phase 3 trace
    /// 实现后自动升级 `Full`。
    fn take_snapshot(&self) -> HeapSnapshot;

    /// 遍历当前所有存活对象（同 snapshot 的覆盖范围限制）。
    fn iterate_live_objects(&self, visitor: &mut dyn FnMut(&Value));

    // ── 11. Stats ────────────────────────────────────────────────────────────

    fn stats(&self) -> HeapStats;
}

#[cfg(test)]
#[path = "heap_tests.rs"]
mod heap_tests;
