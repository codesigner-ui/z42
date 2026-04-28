//! `RcMagrGC` —— Phase 1 GC backend with full embedding API surface.
//!
//! 通过 [`GcRef<T>`] 句柄抽象走 `Rc<RefCell<T>>` backing（保留引用相等 /
//! 身份哈希 / 内部可变性语义），同时实现 MMTk porting contract 形状的全部
//! host-side 嵌入接口（roots / observers / profiler / weak refs / finalizers /
//! heap config / ...）。
//!
//! **Phase 1 已知限制**：
//! 1. **环引用泄漏**：`a.next = b; b.next = a` 仍泄漏 → Phase 2 修复
//! 2. **Finalizer 不会被自动触发**：RC 缺 Drop hook，注册被记录但不调用 →
//!    Phase 3 mark-sweep 调度真实触发
//! 3. **`take_snapshot` / `iterate_live_objects` 仅覆盖 reachable from pinned
//!    roots**：RC 无全堆枚举能力 → Phase 3 trace 后自动升级 Full
//! 4. **`used_bytes` 单调递增**：RC drop 不可观察 → Phase 3 trace 精确化
//! 5. **`OutOfMemory` 仅通知不拒绝**：RC 模式 alloc 仍然成功 → Phase 3 可拒绝

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

    /// alloc 通用通路：bump stats + 检查压力 + 触发 sampler。
    fn record_alloc(&self, kind: AllocKind, size: usize) {
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
        self.record_alloc(AllocKind::Object { class }, size);
        value
    }

    fn alloc_array(&self, elems: Vec<Value>) -> Value {
        let elem_count = elems.len();
        let value      = Value::Array(GcRef::new(elems));
        let size       = self.object_size_bytes(&value);
        self.record_alloc(AllocKind::Array { elem_count }, size);
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
        let used = self.inner.borrow().stats.used_bytes;
        self.fire_event(GcEvent::BeforeCollect {
            kind: GcKind::CycleCollector, used_bytes: used,
        });
        self.inner.borrow_mut().stats.gc_cycles += 1;
        self.fire_event(GcEvent::AfterCollect {
            kind: GcKind::CycleCollector, freed_bytes: 0, pause_us: 0,
        });
    }

    fn force_collect(&self) -> CollectStats {
        if self.inner.borrow().pause_count > 0 {
            return CollectStats::default();
        }
        let used = self.inner.borrow().stats.used_bytes;
        self.fire_event(GcEvent::BeforeCollect {
            kind: GcKind::Full, used_bytes: used,
        });
        self.inner.borrow_mut().stats.gc_cycles += 1;
        self.fire_event(GcEvent::AfterCollect {
            kind: GcKind::Full, freed_bytes: 0, pause_us: 0,
        });
        CollectStats {
            freed_bytes: 0, pause_us: 0, kind: Some(GcKind::Full),
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
        let mut snapshot = HeapSnapshot {
            coverage:     SnapshotCoverage::ReachableFromPinnedRoots,
            timestamp_us: Self::now_us(),
            ..Default::default()
        };
        let mut visited: HashSet<usize> = HashSet::new();
        let mut queue: Vec<Value> = self.inner.borrow().roots.values().cloned().collect();

        while let Some(v) = queue.pop() {
            let Some(key) = Self::rc_ptr_key(&v) else { continue };
            if !visited.insert(key) { continue }
            let size = self.object_size_bytes(&v) as u64;
            let Some(type_name) = Self::type_name_of(&v) else { continue };
            let entry = snapshot.objects_by_type.entry(type_name).or_default();
            entry.count += 1;
            entry.bytes += size;
            snapshot.total_objects += 1;
            snapshot.total_bytes   += size;
            self.scan_object_refs(&v, &mut |child| {
                if Self::rc_ptr_key(child).is_some() {
                    queue.push(child.clone());
                }
            });
        }
        snapshot
    }

    fn iterate_live_objects(&self, visitor: &mut dyn FnMut(&Value)) {
        let mut visited: HashSet<usize> = HashSet::new();
        let mut queue: Vec<Value> = self.inner.borrow().roots.values().cloned().collect();
        while let Some(v) = queue.pop() {
            let Some(key) = Self::rc_ptr_key(&v) else { continue };
            if !visited.insert(key) { continue }
            visitor(&v);
            self.scan_object_refs(&v, &mut |child| {
                if Self::rc_ptr_key(child).is_some() {
                    queue.push(child.clone());
                }
            });
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
