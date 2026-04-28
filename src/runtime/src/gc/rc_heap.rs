//! `RcMagrGC` —— Phase 1 GC backend with full embedding API surface.
//!
//! 通过 [`GcRef<T>`] 句柄抽象走 `Rc<RefCell<T>>` backing（保留引用相等 /
//! 身份哈希 / 内部可变性语义），同时实现 MMTk porting contract 形状的全部
//! host-side 嵌入接口（roots / observers / profiler / weak refs / finalizers /
//! heap config / ...）。
//!
//! **Phase 3a/3b/3c 后已知限制**：
//! 1. **Finalizer 不会被自动触发**：RC 缺 Drop hook，注册被记录但不调用 →
//!    Phase 3d 调度真实触发（cycle collector sweep 时回调）
//! 2. **`OutOfMemory` 仅通知不拒绝**：RC 模式 alloc 仍然成功 → Phase 3e 可拒绝
//! 3. **`collect_cycles` 必须在 interp/JIT 不在执行中调用**：用户代码中 Rust 局部
//!    变量持的 Value 不在 GC roots 中，环检测看不到 → 可能误把外部仍持有的对象
//!    判为可断 → Phase 3f Cranelift stack maps 解决
//!
//! **已解决**：
//! - Phase 3b（2026-04-29 add-heap-registry）：`take_snapshot` /
//!   `iterate_live_objects` 升级 Full coverage
//! - Phase 3c（2026-04-29 add-cycle-breaking-collector）：环引用真实回收
//!   （Bacon-Rajan trial-deletion，断环让 Rc Drop 链式释放）+ `used_bytes`
//!   在 collect 后准确反映释放量

use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::sync::{Arc, OnceLock};
use std::time::Instant;

use crate::metadata::{NativeData, ScriptObject, TypeDesc, Value};

use super::heap::MagrGC;
use super::refs::GcRef;
use super::types::{
    AllocKind, AllocSample, AllocSamplerFn, CollectStats, FinalizerFn, FrameMark,
    GcEvent, GcKind, GcObserver, HeapSnapshot, HeapStats, ObserverId, RootHandle,
    SnapshotCoverage, WeakRef, WeakRefInner,
};

// ── Internal state ───────────────────────────────────────────────────────────

#[derive(Default)]
struct RcHeapInner {
    stats:             HeapStats,
    roots:             HashMap<RootHandle, Value>,
    /// 每个 frame 的 pin 列表，用于 leave_frame 时整批 unpin。
    frame_pins:        Vec<Vec<RootHandle>>,
    observers:         Vec<(ObserverId, Arc<dyn GcObserver>)>,
    /// Finalizers 按 `GcRef::as_ptr(gc) as usize` 索引；Phase 1 仅注册不触发。
    finalizers:        HashMap<usize, FinalizerFn>,
    alloc_sampler:     Option<AllocSamplerFn>,
    pause_count:       u32,
    next_root_id:      u64,
    next_observer_id:  u64,
    /// 防止 NearHeapLimit 事件刷屏（RC 模式 used_bytes 单调，这个 flag 也单调）。
    near_limit_warned: bool,
    /// **Phase 3b: heap registry** —— 每次 `alloc_*` 推入对应 WeakRef，让 GC
    /// 能枚举所有"曾经分配且当前可能存活"的堆对象。这是 Phase 3c mark-sweep
    /// 的物理前置：mark 阶段需要候选集（roots 之外的所有对象）。
    /// 不阻止对象回收（Weak 不持 strong refcount）。
    heap_registry:     Vec<WeakRef>,
}

impl std::fmt::Debug for RcHeapInner {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RcHeapInner")
            .field("stats",             &self.stats)
            .field("roots_count",       &self.roots.len())
            .field("frame_count",       &self.frame_pins.len())
            .field("observers_count",   &self.observers.len())
            .field("finalizers_count",  &self.finalizers.len())
            .field("alloc_sampler",     &self.alloc_sampler.is_some())
            .field("pause_count",       &self.pause_count)
            .field("near_limit_warned", &self.near_limit_warned)
            .field("registry_size",     &self.heap_registry.len())
            .finish()
    }
}

// ── RcMagrGC ─────────────────────────────────────────────────────────────────

#[derive(Default)]
pub struct RcMagrGC {
    inner: RefCell<RcHeapInner>,
}

impl std::fmt::Debug for RcMagrGC {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self.inner.try_borrow() {
            Ok(i)  => f.debug_struct("RcMagrGC").field("inner", &*i).finish(),
            Err(_) => f.debug_struct("RcMagrGC").field("inner", &"<borrowed>").finish(),
        }
    }
}

impl RcMagrGC {
    pub fn new() -> Self { Self::default() }

    fn now_us() -> u64 {
        static EPOCH: OnceLock<Instant> = OnceLock::new();
        EPOCH.get_or_init(Instant::now).elapsed().as_micros() as u64
    }

    /// 提取 heap 引用类型 Value 的指针 key（用于去重 / finalizer key）。
    fn rc_ptr_key(value: &Value) -> Option<usize> {
        match value {
            Value::Object(gc) => Some(GcRef::as_ptr(gc) as *const _ as usize),
            Value::Array(gc)  => Some(GcRef::as_ptr(gc) as *const _ as usize),
            _ => None,
        }
    }

    /// 取 Value 的"类型名"（用于 snapshot 聚合）。
    fn type_name_of(value: &Value) -> Option<String> {
        match value {
            Value::Object(rc) => Some(rc.borrow().type_desc.name.clone()),
            Value::Array(_)   => Some("<Array>".to_string()),
            _ => None,
        }
    }

    /// 把事件分发到所有 observer。先 snapshot observer 列表再分发，避免
    /// observer 在回调中重入 add_observer/remove_observer 引发 borrow 冲突。
    fn fire_event(&self, event: GcEvent) {
        let observers: Vec<_> = self.inner.borrow().observers.iter()
            .map(|(_, o)| Arc::clone(o)).collect();
        for o in observers {
            o.on_event(&event);
        }
    }

    /// alloc 通用通路：注册到 registry + bump stats + 检查压力 + 触发 sampler。
    fn record_alloc(&self, value: &Value, kind: AllocKind, size: usize) {
        // 0. 注册到 heap_registry（Phase 3b）
        if let Some(weak) = self.make_weak_internal(value) {
            self.inner.borrow_mut().heap_registry.push(weak);
        }
        // 1. 更新 stats（先借再放，避免后续触发事件时 borrow 冲突）
        {
            let mut i = self.inner.borrow_mut();
            i.stats.allocations += 1;
            i.stats.used_bytes  = i.stats.used_bytes.saturating_add(size as u64);
        }
        // 2. 压力检查（可能触发 GcEvent）
        self.check_pressure(size as u64);
        // 3. Sampler 调度
        let sampler = self.inner.borrow().alloc_sampler.clone();
        if let Some(s) = sampler {
            s(&AllocSample {
                kind,
                size_bytes: size,
                timestamp_us: Self::now_us(),
            });
        }
    }

    /// 内部版 make_weak —— 复制 trait 实现避免在 trait dispatch 路径递归借用。
    fn make_weak_internal(&self, value: &Value) -> Option<WeakRef> {
        match value {
            Value::Object(gc) => Some(WeakRef {
                inner: WeakRefInner::Object(GcRef::downgrade(gc)),
            }),
            Value::Array(gc) => Some(WeakRef {
                inner: WeakRefInner::Array(GcRef::downgrade(gc)),
            }),
            _ => None,
        }
    }

    /// 内部版 upgrade_weak。
    fn upgrade_weak_internal(weak: &WeakRef) -> Option<Value> {
        match &weak.inner {
            WeakRefInner::Object(w) => w.upgrade().map(Value::Object),
            WeakRefInner::Array (w) => w.upgrade().map(Value::Array),
        }
    }

    // ── Cycle collection helpers (Phase 3c) ──────────────────────────────────

    /// Mark phase: BFS from pinned roots, return reachable pointer-key set.
    ///
    /// **限制**：本 MVP 只扫 pinned roots（host 通过 `pin_root` / `enter_frame`
    /// 注册的）。VmContext 内的 `static_fields` / `pending_exception` / interp
    /// 栈帧 regs 暂未对接 → 用户必须保证 `collect_cycles` 在 VM 顶层调用之间
    /// 触发，或显式 pin 跨调用持有的 Value。Phase 3f Cranelift stack maps 解决。
    fn mark_reachable_set(&self) -> HashSet<usize> {
        let mut reachable: HashSet<usize> = HashSet::new();
        let mut queue: Vec<Value> = self.inner.borrow().roots.values().cloned().collect();
        while let Some(v) = queue.pop() {
            let Some(key) = Self::rc_ptr_key(&v) else { continue };
            if !reachable.insert(key) { continue; }
            self.scan_object_refs(&v, &mut |child| {
                if Self::rc_ptr_key(child).is_some() {
                    queue.push(child.clone());
                }
            });
        }
        reachable
    }

    /// 断环：清空对象内部引用，让被引用方 Rc::strong_count 减一。
    /// Object → 所有 slots 设 `Value::Null`；Array → `vec.clear()`。
    fn break_cycle_value(v: &Value) {
        match v {
            Value::Object(gc) => {
                for slot in gc.borrow_mut().slots.iter_mut() {
                    *slot = Value::Null;
                }
            }
            Value::Array(gc) => {
                gc.borrow_mut().clear();
            }
            _ => {}
        }
    }

    /// Trial-deletion cycle collection（Bacon-Rajan 简化版）：
    ///
    /// 1. **Mark**：从 roots 出发遍历 reachable
    /// 2. **Snapshot alive**：registry 中所有存活对象（含 reachable 与 unreachable）
    /// 3. **Filter**：alive ∖ reachable = unreachable 候选集
    /// 4. **Trial deletion**：对每个 v∈unreachable，
    ///    `tentative[v] = strong_count(v) - 1` （减去本函数 alive_vec 持的强引用）；
    ///    再遍历 unreachable 中每个 v 的子引用，若子引用也在 unreachable，
    ///    `tentative[child] -= 1`。最终 `tentative[v]` = v 来自 unreachable
    ///    集合**外部**的强引用数（即 root 之外、user 代码持有的）。
    /// 5. **Break**：`tentative[v] == 0` 的 v 是纯环内对象 → 调 `break_cycle_value`
    ///    清空内部引用，让其指向的对象 Rc 引用减一。
    /// 6. 函数返回时 alive_vec 自然 drop，断环后 Rc 链式 Drop 释放内存。
    ///
    /// 返回估算的 freed_bytes（被断环对象的 object_size_bytes 之和）。注意：实际
    /// 物理释放可能延后到 alive_vec drop 之后；某些 Rc 还可能由 user 持有但
    /// 在下一次 collect / drop 时才彻底释放。这是 RC backing 的固有性质。
    fn run_cycle_collection(&self) -> u64 {
        let reachable = self.mark_reachable_set();
        let alive = self.snapshot_live_from_registry();

        let unreachable: Vec<Value> = alive.into_iter()
            .filter(|v| match Self::rc_ptr_key(v) {
                Some(k) => !reachable.contains(&k),
                None => false,
            })
            .collect();

        if unreachable.is_empty() {
            return 0;
        }

        // Trial deletion: tentative[v] = strong_count(v) - 1 (alive_vec hold)
        let mut tentative: HashMap<usize, i64> = HashMap::with_capacity(unreachable.len());
        for v in &unreachable {
            let Some(key) = Self::rc_ptr_key(v) else { continue };
            let count = match v {
                Value::Object(gc) => GcRef::strong_count(gc),
                Value::Array(gc)  => GcRef::strong_count(gc),
                _ => continue,
            };
            tentative.insert(key, count as i64 - 1);
        }
        // 减去 unreachable 集合内部的引用（child 仅在 tentative 中时减）
        for v in &unreachable {
            self.scan_object_refs(v, &mut |child| {
                if let Some(child_key) = Self::rc_ptr_key(child) {
                    if let Some(t) = tentative.get_mut(&child_key) {
                        *t -= 1;
                    }
                }
            });
        }

        // Break: tentative <= 0 表示无外部引用，安全断环
        let mut freed_bytes: u64 = 0;
        for v in &unreachable {
            let Some(key) = Self::rc_ptr_key(v) else { continue };
            if tentative.get(&key).copied().unwrap_or(0) <= 0 {
                freed_bytes = freed_bytes.saturating_add(self.object_size_bytes(v) as u64);
                Self::break_cycle_value(v);
            }
        }

        // unreachable Vec drops here → 断环对象的 Rc 强引用计数链式归零 → Drop
        freed_bytes
    }

    /// Snapshot 当前 registry 中所有仍存活的 Value，并就地 prune 掉死引用。
    /// 返回值已去重（同一对象只出现一次）。
    fn snapshot_live_from_registry(&self) -> Vec<Value> {
        let mut i = self.inner.borrow_mut();
        let mut alive: Vec<Value> = Vec::with_capacity(i.heap_registry.len());
        let mut visited: HashSet<usize> = HashSet::new();
        // 同步 prune：retain 只保留还能 upgrade 的项
        i.heap_registry.retain(|weak| {
            if let Some(v) = Self::upgrade_weak_internal(weak) {
                if let Some(key) = Self::rc_ptr_key(&v) {
                    if visited.insert(key) {
                        alive.push(v);
                    }
                }
                true
            } else {
                false
            }
        });
        alive
    }

    fn check_pressure(&self, requested: u64) {
        let (used, max, near_warned) = {
            let i = self.inner.borrow();
            (i.stats.used_bytes, i.stats.max_bytes, i.near_limit_warned)
        };
        let Some(limit) = max else { return };
        let near_threshold     = (limit as f64 * 0.9 ) as u64;
        let pressure_threshold = (limit as f64 * 0.75) as u64;

        if !near_warned && used >= near_threshold {
            self.inner.borrow_mut().near_limit_warned = true;
            self.fire_event(GcEvent::NearHeapLimit {
                used_bytes: used, limit_bytes: limit,
            });
        } else if used >= pressure_threshold && used < near_threshold {
            self.fire_event(GcEvent::AllocationPressure {
                used_bytes: used, limit_bytes: limit,
            });
        }

        if used > limit {
            self.fire_event(GcEvent::OutOfMemory {
                requested_bytes: requested, limit_bytes: limit,
            });
        }
    }
}

// ── trait impl ───────────────────────────────────────────────────────────────

impl MagrGC for RcMagrGC {
    // ── 1. Allocation ────────────────────────────────────────────────────────

    fn alloc_object(
        &self,
        type_desc: Arc<TypeDesc>,
        slots: Vec<Value>,
        native: NativeData,
    ) -> Value {
        let class = type_desc.name.clone();
        let value = Value::Object(GcRef::new(ScriptObject {
            type_desc, slots, native,
        }));
        let size = self.object_size_bytes(&value);
        self.record_alloc(&value, AllocKind::Object { class }, size);
        value
    }

    fn alloc_array(&self, elems: Vec<Value>) -> Value {
        let elem_count = elems.len();
        let value      = Value::Array(GcRef::new(elems));
        let size       = self.object_size_bytes(&value);
        self.record_alloc(&value, AllocKind::Array { elem_count }, size);
        value
    }

    // ── 2. Roots ─────────────────────────────────────────────────────────────

    fn pin_root(&self, value: Value) -> RootHandle {
        let mut i = self.inner.borrow_mut();
        let handle = RootHandle(i.next_root_id);
        i.next_root_id += 1;
        i.roots.insert(handle, value);
        i.stats.roots_pinned += 1;
        if let Some(pins) = i.frame_pins.last_mut() {
            pins.push(handle);
        }
        handle
    }

    fn unpin_root(&self, handle: RootHandle) {
        let mut i = self.inner.borrow_mut();
        if i.roots.remove(&handle).is_some() {
            i.stats.roots_pinned = i.stats.roots_pinned.saturating_sub(1);
        }
    }

    fn enter_frame(&self) -> FrameMark {
        let mut i = self.inner.borrow_mut();
        let depth = i.frame_pins.len() as u32;
        i.frame_pins.push(Vec::new());
        FrameMark(depth)
    }

    fn leave_frame(&self, mark: FrameMark) {
        let mut i = self.inner.borrow_mut();
        while i.frame_pins.len() as u32 > mark.0 {
            if let Some(pins) = i.frame_pins.pop() {
                for h in pins {
                    if i.roots.remove(&h).is_some() {
                        i.stats.roots_pinned = i.stats.roots_pinned.saturating_sub(1);
                    }
                }
            }
        }
    }

    fn for_each_root(&self, visitor: &mut dyn FnMut(&Value)) {
        let i = self.inner.borrow();
        for v in i.roots.values() {
            visitor(v);
        }
    }

    // ── 3. Write barriers (default no-op via trait) ──────────────────────────
    // (RcMagrGC 不重载；trait 默认实现已经是 no-op)

    // ── 4. Object Model ──────────────────────────────────────────────────────

    fn object_size_bytes(&self, value: &Value) -> usize {
        use std::mem::size_of;
        match value {
            Value::Null | Value::Bool(_) | Value::Char(_)
            | Value::I64(_) | Value::F64(_) => size_of::<Value>(),
            Value::Str(s) => size_of::<Value>() + s.capacity(),
            Value::Array(rc) => {
                size_of::<Value>() + size_of::<Vec<Value>>()
                    + rc.borrow().capacity() * size_of::<Value>()
            }
            Value::Object(rc) => {
                let obj = rc.borrow();
                size_of::<Value>() + size_of::<ScriptObject>()
                    + obj.slots.capacity() * size_of::<Value>()
            }
        }
    }

    fn scan_object_refs(&self, value: &Value, visitor: &mut dyn FnMut(&Value)) {
        match value {
            Value::Object(rc) => {
                let obj = rc.borrow();
                for slot in &obj.slots { visitor(slot); }
            }
            Value::Array(rc) => {
                let arr = rc.borrow();
                for elem in arr.iter() { visitor(elem); }
            }
            _ => {}
        }
    }

    // ── 5. Collection control ────────────────────────────────────────────────

    fn collect_cycles(&self) {
        if self.inner.borrow().pause_count > 0 { return; }
        let start = Self::now_us();
        let used_before = self.inner.borrow().stats.used_bytes;
        self.fire_event(GcEvent::BeforeCollect {
            kind: GcKind::CycleCollector, used_bytes: used_before,
        });
        let freed_bytes = self.run_cycle_collection();
        {
            let mut i = self.inner.borrow_mut();
            i.stats.gc_cycles += 1;
            i.stats.used_bytes = i.stats.used_bytes.saturating_sub(freed_bytes);
        }
        let pause_us = Self::now_us().saturating_sub(start);
        self.fire_event(GcEvent::AfterCollect {
            kind: GcKind::CycleCollector, freed_bytes, pause_us,
        });
    }

    fn force_collect(&self) -> CollectStats {
        if self.inner.borrow().pause_count > 0 {
            return CollectStats::default();
        }
        let start = Self::now_us();
        let used_before = self.inner.borrow().stats.used_bytes;
        self.fire_event(GcEvent::BeforeCollect {
            kind: GcKind::Full, used_bytes: used_before,
        });
        let freed_bytes = self.run_cycle_collection();
        {
            let mut i = self.inner.borrow_mut();
            i.stats.gc_cycles += 1;
            i.stats.used_bytes = i.stats.used_bytes.saturating_sub(freed_bytes);
        }
        let pause_us = Self::now_us().saturating_sub(start);
        self.fire_event(GcEvent::AfterCollect {
            kind: GcKind::Full, freed_bytes, pause_us,
        });
        CollectStats {
            freed_bytes, pause_us, kind: Some(GcKind::Full),
        }
    }

    fn pause(&self)  { self.inner.borrow_mut().pause_count += 1; }
    fn resume(&self) {
        let mut i = self.inner.borrow_mut();
        i.pause_count = i.pause_count.saturating_sub(1);
    }

    // ── 6. Heap config ───────────────────────────────────────────────────────

    fn set_max_heap_bytes(&self, max: Option<u64>) {
        let mut i = self.inner.borrow_mut();
        i.stats.max_bytes      = max;
        i.near_limit_warned    = false; // reset 让新阈值能再次触发 NearHeapLimit
    }

    fn used_bytes(&self) -> u64 {
        self.inner.borrow().stats.used_bytes
    }

    // ── 7. Finalization ──────────────────────────────────────────────────────

    fn register_finalizer(&self, value: &Value, fin: FinalizerFn) {
        let Some(key) = Self::rc_ptr_key(value) else { return };
        let mut i = self.inner.borrow_mut();
        i.finalizers.insert(key, fin);
        i.stats.finalizers_pending = i.finalizers.len() as u64;
    }

    fn cancel_finalizer(&self, value: &Value) {
        let Some(key) = Self::rc_ptr_key(value) else { return };
        let mut i = self.inner.borrow_mut();
        i.finalizers.remove(&key);
        i.stats.finalizers_pending = i.finalizers.len() as u64;
    }

    // ── 8. Weak references ───────────────────────────────────────────────────

    fn make_weak(&self, value: &Value) -> Option<WeakRef> {
        match value {
            Value::Object(gc) => Some(WeakRef {
                inner: WeakRefInner::Object(GcRef::downgrade(gc)),
            }),
            Value::Array(gc) => Some(WeakRef {
                inner: WeakRefInner::Array(GcRef::downgrade(gc)),
            }),
            _ => None,
        }
    }

    fn upgrade_weak(&self, weak: &WeakRef) -> Option<Value> {
        match &weak.inner {
            WeakRefInner::Object(w) => w.upgrade().map(Value::Object),
            WeakRefInner::Array (w) => w.upgrade().map(Value::Array),
        }
    }

    // ── 9. Event observers ───────────────────────────────────────────────────

    fn add_observer(&self, observer: Arc<dyn GcObserver>) -> ObserverId {
        let mut i = self.inner.borrow_mut();
        let id = ObserverId(i.next_observer_id);
        i.next_observer_id += 1;
        i.observers.push((id, observer));
        i.stats.observers = i.observers.len() as u64;
        id
    }

    fn remove_observer(&self, id: ObserverId) {
        let mut i = self.inner.borrow_mut();
        i.observers.retain(|(o_id, _)| *o_id != id);
        i.stats.observers = i.observers.len() as u64;
    }

    // ── 10. Profiler ─────────────────────────────────────────────────────────

    fn set_alloc_sampler(&self, sampler: Option<AllocSamplerFn>) {
        self.inner.borrow_mut().alloc_sampler = sampler;
    }

    fn take_snapshot(&self) -> HeapSnapshot {
        // Phase 3b: 直接遍历 heap_registry，覆盖范围升级为 Full（所有 alloc 过且
        // 当前仍 strong-reachable 的对象，不依赖 host pin）。
        let mut snapshot = HeapSnapshot {
            coverage:     SnapshotCoverage::Full,
            timestamp_us: Self::now_us(),
            ..Default::default()
        };
        for v in self.snapshot_live_from_registry() {
            let size = self.object_size_bytes(&v) as u64;
            let Some(type_name) = Self::type_name_of(&v) else { continue };
            let entry = snapshot.objects_by_type.entry(type_name).or_default();
            entry.count += 1;
            entry.bytes += size;
            snapshot.total_objects += 1;
            snapshot.total_bytes   += size;
        }
        snapshot
    }

    fn iterate_live_objects(&self, visitor: &mut dyn FnMut(&Value)) {
        // Phase 3b: registry-driven Full coverage. 同对象只访问一次（registry
        // snapshot 内部去重 by GcRef::as_ptr）。
        for v in self.snapshot_live_from_registry() {
            visitor(&v);
        }
    }

    // ── 11. Stats ────────────────────────────────────────────────────────────

    fn stats(&self) -> HeapStats {
        self.inner.borrow().stats
    }
}

#[cfg(test)]
#[path = "rc_heap_tests.rs"]
mod rc_heap_tests;
