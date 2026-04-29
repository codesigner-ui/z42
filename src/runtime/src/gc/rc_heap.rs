//! `RcMagrGC` —— Phase 1 GC backend with full embedding API surface.
//!
//! 通过 [`GcRef<T>`] 句柄抽象走 `Rc<RefCell<T>>` backing（保留引用相等 /
//! 身份哈希 / 内部可变性语义），同时实现 MMTk porting contract 形状的全部
//! host-side 嵌入接口（roots / observers / profiler / weak refs / finalizers /
//! heap config / ...）。
//!
//! **Phase 3a/3b/3c/3d/3d.1/3f/3e/3f-2 后已知限制**：
//! 1. **`OutOfMemory` 仅通知不拒绝**：MagrGC trait `alloc_*` 返回 `Value` 不带
//!    Result，签名约束。真拒绝需 trait API 升级 + 全 callsite 错误处理路径
//!
//! **已解决**：
//! - Phase 3b（add-heap-registry）：snapshot/iterate Full coverage
//! - Phase 3c（add-cycle-breaking-collector）：环引用真实回收 + `used_bytes` 精确
//! - Phase 3d（add-finalizer-and-auto-collect）：finalizer 真触发 + 内存压力自动 collect
//! - Phase 3d.1（add-external-root-scanning）：**external root scanner 机制 +
//!   VmContext 的 `static_fields` / `pending_exception` 自动暴露为 GC roots**，
//!   修复 cycle collector 漏扫导致 static 字段持有的对象被误清的 bug
//! - Phase 3f（add-interp-stack-scanning）：interp `exec_function` 通过
//!   `FrameGuard` RAII 把 `frame.regs` Vec 指针注册到 `VmContext.exec_stack`，
//!   scanner 闭包遍历喂给 mark 阶段。修复脚本执行中调 GC 时
//!   "outer 在 reg + outer.slot → inner 间接可达 → inner 被误清" 的 bug
//! - Phase 3e（add-drop-time-finalizer）：`GcRef<T>` backing 升级为
//!   `Rc<GcAllocation<T>>`，wrapper 含 `finalizer: RefCell<Option<FinalizerFn>>`
//!   + 自定义 `Drop`。**所有 Rc Drop 路径**（含纯链式 drop / cycle 断环
//!   后 alive_vec drop / 普通 scope 退出）都自动触发已注册 finalizer，
//!   one-shot via take。`finalizers: HashMap` 字段移除，`stats()` 即时遍历
//!   registry 重算 finalizers_pending。
//! - Phase 3f-2（add-jit-stack-scanning）：6 个 JitFrame::new callsite 在
//!   jit_fn 调用前后 push/pop frame.regs 到 VmContext.exec_stack（与 interp
//!   共用同一数据结构）。修复 JIT 路径下 transitive 可达对象（如返回值穿过
//!   函数边界后通过 outer.slot 间接持有）被误清的 bug。

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
    alloc_sampler:     Option<AllocSamplerFn>,
    pause_count:       u32,
    next_root_id:      u64,
    next_observer_id:  u64,
    /// 防止 NearHeapLimit 事件刷屏（Phase 3d 后 collect_cycles 完成且使用降到
    /// 阈值以下时会自动 reset，下次跨阈值能再发事件）。
    near_limit_warned: bool,
    /// **Phase 3b: heap registry** —— 每次 `alloc_*` 推入对应 WeakRef，让 GC
    /// 能枚举所有"曾经分配且当前可能存活"的堆对象。这是 Phase 3c mark-sweep
    /// 的物理前置：mark 阶段需要候选集（roots 之外的所有对象）。
    /// 不阻止对象回收（Weak 不持 strong refcount）。
    heap_registry:     Vec<WeakRef>,
    /// **Phase 3d**: 上次 auto-collect 触发时的 `used_bytes`，用于 throttle
    /// 自动 collect —— 仅当当前 used 距上次增长 >= 10% limit 才再次自动触发。
    last_auto_collect_used: u64,
    // **Phase 3e**: finalizers 不再集中存 HashMap；改存到每个 GcAllocation 的
    // finalizer Cell 上。Drop 时自动 take + fire（含 cycle 断环后 alive_vec
    // drop 链）。register_finalizer / cancel_finalizer 走 GcRef 方法。
    // finalizers_pending 由 stats() 即时遍历 registry 重算。
}

impl std::fmt::Debug for RcHeapInner {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RcHeapInner")
            .field("stats",             &self.stats)
            .field("roots_count",       &self.roots.len())
            .field("frame_count",       &self.frame_pins.len())
            .field("observers_count",   &self.observers.len())
            .field("alloc_sampler",     &self.alloc_sampler.is_some())
            .field("pause_count",       &self.pause_count)
            .field("near_limit_warned", &self.near_limit_warned)
            .field("registry_size",     &self.heap_registry.len())
            .field("last_auto_collect_used", &self.last_auto_collect_used)
            .finish()
    }
}

// ── RcMagrGC ─────────────────────────────────────────────────────────────────

/// External root scanner type. **Phase 3d.1**：宿主（典型情况是 `VmContext`）
/// 通过 `set_external_root_scanner` 注册的闭包，在 mark 阶段被调用以暴露
/// 自己持有的 Value（如 static_fields / pending_exception / 未来的 interp
/// 栈帧 regs），让 cycle collector 不会把这些可达对象误判为 unreachable。
///
/// 不要求 Send + Sync —— 闭包通常捕获 `Rc<RefCell<...>>` 共享 VmContext 字段，
/// 与 RcMagrGC 同处单一线程下使用。
type ExternalRootScanner = Box<dyn Fn(&mut dyn FnMut(&Value))>;

#[derive(Default)]
pub struct RcMagrGC {
    inner: RefCell<RcHeapInner>,
    external_root_scanner: RefCell<Option<ExternalRootScanner>>,
}

impl std::fmt::Debug for RcMagrGC {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let scanner_set = self.external_root_scanner.try_borrow()
            .map(|s| s.is_some())
            .unwrap_or(false);
        let mut d = f.debug_struct("RcMagrGC");
        match self.inner.try_borrow() {
            Ok(i)  => { d.field("inner", &*i); }
            Err(_) => { d.field("inner", &"<borrowed>"); }
        }
        d.field("external_scanner", &scanner_set).finish()
    }
}

impl RcMagrGC {
    pub fn new() -> Self { Self::default() }

    /// **Phase 3d.1**: 注册一个 external root scanner 闭包。每次 cycle
    /// collection mark 阶段在扫完 pinned roots 后会调用此闭包，把闭包 yield
    /// 出来的 Value 也加入 reachable BFS 队列。
    ///
    /// 典型用途：`VmContext::new` 注册一个扫描自己 static_fields /
    /// pending_exception 的闭包，让那些字段持有的 cyclic 对象在 collect 时
    /// 不被误判为可断。
    ///
    /// 同一 RcMagrGC 上重复调用会**覆盖**之前的 scanner（仅一个 active 闭包）。
    /// 传 `set_external_root_scanner(Box::new(|_| {}))` 等价于卸载（no-op 闭包）。
    pub fn set_external_root_scanner(&self, scanner: ExternalRootScanner) {
        *self.external_root_scanner.borrow_mut() = Some(scanner);
    }

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

    /// Mark phase: BFS from pinned roots **+ external root scanner**, return
    /// reachable pointer-key set.
    ///
    /// **Phase 3d.1**: 现在扫两批 roots：
    /// 1. RcMagrGC 内部 `pinned_roots`（host 通过 `pin_root` / `enter_frame` 注册）
    /// 2. `external_root_scanner` 闭包暴露的额外 roots（典型：VmContext
    ///    的 `static_fields` / `pending_exception` 持有的 Value）
    ///
    /// **剩余限制**：interp / JIT 栈帧的 register 暂未对接 → 用户必须保证
    /// `collect_cycles` 在 VM 顶层调用之间触发，或显式 pin 跨调用持有的
    /// Value（Phase 3f Cranelift stack maps 解决）。
    fn mark_reachable_set(&self) -> HashSet<usize> {
        let mut reachable: HashSet<usize> = HashSet::new();
        let mut queue: Vec<Value> = self.inner.borrow().roots.values().cloned().collect();

        // Phase 3d.1: 把 external scanner 暴露的额外 roots 也压入 queue
        // 注：在 borrow scanner 的 scope 内不持有 self.inner 借用，避免 re-entrant 冲突
        {
            let scanner_borrow = self.external_root_scanner.borrow();
            if let Some(scan) = scanner_borrow.as_ref() {
                scan(&mut |v| {
                    queue.push(v.clone());
                });
            }
        }

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

        // **Phase 3e**: 不再显式 dispatch finalizer —— 当 unreachable Vec 在
        // 函数返回时 drop，断环对象的 Rc 强引用计数链式归零，触发
        // `GcAllocation::Drop` 自动调用注册的 finalizer（one-shot via take）。
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

    /// **Phase 3d**: 内存压力下自动触发 collect_cycles。
    ///
    /// 条件：
    /// - max_bytes 已设
    /// - used >= 90% limit
    /// - 距上次 auto-collect 增长 >= 10% limit（throttle，避免每次 alloc 都 collect）
    /// - pause_count == 0
    fn maybe_auto_collect(&self) {
        let (used, max_opt, last, paused) = {
            let i = self.inner.borrow();
            (i.stats.used_bytes, i.stats.max_bytes, i.last_auto_collect_used, i.pause_count > 0)
        };
        if paused { return; }
        let Some(limit) = max_opt else { return };
        let near_threshold = (limit as f64 * 0.9) as u64;
        if used < near_threshold { return; }
        let throttle_delta = (limit as f64 * 0.1) as u64;
        if used.saturating_sub(last) < throttle_delta { return; }
        self.inner.borrow_mut().last_auto_collect_used = used;
        self.collect_cycles();
    }

    /// **Phase 3d**: collect 完成后，若 used 已降到 90% 阈值以下，
    /// reset `near_limit_warned` 让下次跨阈值能再发 NearHeapLimit 事件。
    fn maybe_reset_near_limit_warned(&self) {
        let mut i = self.inner.borrow_mut();
        let Some(limit) = i.stats.max_bytes else { return };
        let near_threshold = (limit as f64 * 0.9) as u64;
        if i.stats.used_bytes < near_threshold {
            i.near_limit_warned = false;
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
        self.record_alloc(&value, AllocKind::Object { class }, size);
        // Phase 3d: 内存压力下自动 collect（local `value` 持外部强引用，自身不会被破坏）
        self.maybe_auto_collect();
        value
    }

    fn alloc_array(&self, elems: Vec<Value>) -> Value {
        let elem_count = elems.len();
        let value      = Value::Array(GcRef::new(elems));
        let size       = self.object_size_bytes(&value);
        self.record_alloc(&value, AllocKind::Array { elem_count }, size);
        self.maybe_auto_collect();
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
        // Phase 3d: 若 used 已降到 90% 阈值以下，重置 near_limit_warned
        self.maybe_reset_near_limit_warned();
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
        self.maybe_reset_near_limit_warned();
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

    /// **Phase 3e**: finalizer 直接挂在 GcAllocation wrapper 上，Drop 时自动
    /// 触发（含 cycle 断环后 alive_vec drop 链 + 普通 Rc Drop）。
    fn register_finalizer(&self, value: &Value, fin: FinalizerFn) {
        match value {
            Value::Object(gc) => GcRef::set_finalizer(gc, fin),
            Value::Array(gc)  => GcRef::set_finalizer(gc, fin),
            _ => {} // 原子值无 finalizer
        }
    }

    fn cancel_finalizer(&self, value: &Value) {
        match value {
            Value::Object(gc) => { let _ = GcRef::cancel_finalizer(gc); }
            Value::Array(gc)  => { let _ = GcRef::cancel_finalizer(gc); }
            _ => {}
        }
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
        // Phase 3e: finalizers_pending 即时遍历 heap_registry 重算 —— 因为
        // finalizer 现在挂在 GcAllocation 上，Drop 时自动 take，没有集中
        // 计数器；准确值需扫 registry。snapshot_live_from_registry 顺路 prune
        // 死引用。
        let alive = self.snapshot_live_from_registry();
        let pending = alive.iter().filter(|v| match v {
            Value::Object(gc) => GcRef::has_finalizer(gc),
            Value::Array(gc)  => GcRef::has_finalizer(gc),
            _ => false,
        }).count() as u64;

        let mut s = self.inner.borrow().stats;
        s.finalizers_pending = pending;
        s
    }
}

#[cfg(test)]
#[path = "rc_heap_tests.rs"]
mod rc_heap_tests;
