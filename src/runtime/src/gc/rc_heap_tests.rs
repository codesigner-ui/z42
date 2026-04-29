//! `RcMagrGC` 单元测试 —— 覆盖全部 11 个能力组。

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use crate::gc::{GcEvent, GcKind, GcObserver, MagrGC, RcMagrGC, SnapshotCoverage};
use crate::metadata::{NativeData, TypeDesc, Value};

fn dummy_type_desc(name: &str) -> Arc<TypeDesc> {
    Arc::new(TypeDesc {
        name: name.to_string(),
        base_name: None,
        fields: Vec::new(),
        field_index: HashMap::new(),
        vtable: Vec::new(),
        vtable_index: HashMap::new(),
        type_params: vec![],
        type_args: vec![],
        type_param_constraints: vec![],
    })
}

// ── 1. Allocation ────────────────────────────────────────────────────────────

#[test]
fn alloc_object_returns_value_object_with_given_fields() {
    let heap = RcMagrGC::new();
    let td   = dummy_type_desc("Foo");
    let v    = heap.alloc_object(td.clone(), vec![Value::I64(1), Value::I64(2)], NativeData::None);
    let Value::Object(rc) = v else { panic!("expected Value::Object") };
    let borrow = rc.borrow();
    assert_eq!(borrow.type_desc.name, "Foo");
    assert_eq!(borrow.slots.len(), 2);
    assert_eq!(borrow.slots[0], Value::I64(1));
}

#[test]
fn two_alloc_object_calls_return_distinct_rcs() {
    let heap = RcMagrGC::new();
    let td   = dummy_type_desc("Foo");
    let a    = heap.alloc_object(td.clone(), vec![], NativeData::None);
    let b    = heap.alloc_object(td.clone(), vec![], NativeData::None);
    let (Value::Object(ra), Value::Object(rb)) = (a, b) else { panic!() };
    assert!(!crate::gc::GcRef::ptr_eq(&ra, &rb));
}

#[test]
fn alloc_array_returns_value_array_with_given_elems() {
    let heap = RcMagrGC::new();
    let v    = heap.alloc_array(vec![Value::I64(7), Value::I64(8), Value::I64(9)]);
    let Value::Array(rc) = v else { panic!("expected Value::Array") };
    let b = rc.borrow();
    assert_eq!(b.len(), 3);
    assert_eq!(b[0], Value::I64(7));
    assert_eq!(b[2], Value::I64(9));
}

#[test]
fn alloc_array_empty_returns_empty_vec() {
    let heap = RcMagrGC::new();
    let v    = heap.alloc_array(vec![]);
    let Value::Array(rc) = v else { panic!() };
    assert!(rc.borrow().is_empty());
}

// ── 2. Roots ─────────────────────────────────────────────────────────────────

#[test]
fn pin_root_increments_count_and_for_each_visits() {
    let heap = RcMagrGC::new();
    let v = heap.alloc_array(vec![Value::I64(7)]);
    let _h = heap.pin_root(v);
    assert_eq!(heap.stats().roots_pinned, 1);

    let mut count = 0;
    heap.for_each_root(&mut |_v| count += 1);
    assert_eq!(count, 1);
}

#[test]
fn unpin_root_decrements_count() {
    let heap = RcMagrGC::new();
    let v = heap.alloc_array(vec![]);
    let h = heap.pin_root(v);
    heap.unpin_root(h);
    assert_eq!(heap.stats().roots_pinned, 0);
    let mut count = 0;
    heap.for_each_root(&mut |_| count += 1);
    assert_eq!(count, 0);
}

#[test]
fn enter_leave_frame_drops_intra_frame_pins() {
    let heap = RcMagrGC::new();
    let mark = heap.enter_frame();
    let _ = heap.pin_root(heap.alloc_array(vec![]));
    let _ = heap.pin_root(heap.alloc_array(vec![]));
    assert_eq!(heap.stats().roots_pinned, 2);
    heap.leave_frame(mark);
    assert_eq!(heap.stats().roots_pinned, 0);
}

#[test]
fn nested_frames_unwind_correctly() {
    let heap = RcMagrGC::new();
    let m1 = heap.enter_frame();
    let _  = heap.pin_root(heap.alloc_array(vec![]));
    let m2 = heap.enter_frame();
    let _  = heap.pin_root(heap.alloc_array(vec![]));
    let _  = heap.pin_root(heap.alloc_array(vec![]));
    assert_eq!(heap.stats().roots_pinned, 3);
    heap.leave_frame(m2);
    assert_eq!(heap.stats().roots_pinned, 1);
    heap.leave_frame(m1);
    assert_eq!(heap.stats().roots_pinned, 0);
}

#[test]
fn pin_outside_frame_persists() {
    let heap = RcMagrGC::new();
    let v = heap.alloc_array(vec![]);
    let _h = heap.pin_root(v);
    let mark = heap.enter_frame();
    heap.leave_frame(mark);
    // Outside-frame pin survives leave_frame
    assert_eq!(heap.stats().roots_pinned, 1);
}

// ── 4. Object Model ──────────────────────────────────────────────────────────

#[test]
fn object_size_bytes_atomic_returns_value_size() {
    let heap = RcMagrGC::new();
    let primitives = vec![
        Value::I64(0), Value::F64(0.0), Value::Bool(true), Value::Char('a'), Value::Null,
    ];
    let expected = std::mem::size_of::<Value>();
    for p in primitives {
        assert_eq!(heap.object_size_bytes(&p), expected);
    }
}

#[test]
fn object_size_bytes_string_includes_capacity() {
    let heap = RcMagrGC::new();
    let s = Value::Str("hello".to_string());
    assert!(heap.object_size_bytes(&s) > std::mem::size_of::<Value>());
}

#[test]
fn scan_object_refs_visits_every_slot() {
    let heap = RcMagrGC::new();
    let v = heap.alloc_object(
        dummy_type_desc("Foo"),
        vec![Value::I64(1), Value::I64(2), Value::I64(3)],
        NativeData::None,
    );
    let mut count = 0;
    heap.scan_object_refs(&v, &mut |_| count += 1);
    assert_eq!(count, 3);
}

#[test]
fn scan_object_refs_visits_every_array_elem() {
    let heap = RcMagrGC::new();
    let v = heap.alloc_array(vec![Value::I64(1); 5]);
    let mut count = 0;
    heap.scan_object_refs(&v, &mut |_| count += 1);
    assert_eq!(count, 5);
}

#[test]
fn scan_object_refs_no_op_on_atomic() {
    let heap = RcMagrGC::new();
    let mut count = 0;
    heap.scan_object_refs(&Value::I64(42), &mut |_| count += 1);
    heap.scan_object_refs(&Value::Str("x".into()), &mut |_| count += 1);
    heap.scan_object_refs(&Value::Null, &mut |_| count += 1);
    assert_eq!(count, 0);
}

// ── 5. Collection control ────────────────────────────────────────────────────

#[test]
fn force_collect_returns_full_kind() {
    let heap = RcMagrGC::new();
    let stats = heap.force_collect();
    assert_eq!(stats.kind, Some(GcKind::Full));
    assert_eq!(stats.freed_bytes, 0);
    assert_eq!(heap.stats().gc_cycles, 1);
}

#[test]
fn pause_skips_force_collect() {
    let heap = RcMagrGC::new();
    heap.pause();
    let stats = heap.force_collect();
    assert_eq!(stats.kind, None);  // skipped
    assert_eq!(heap.stats().gc_cycles, 0);  // not incremented
}

#[test]
fn resume_after_pause_re_enables_collect() {
    let heap = RcMagrGC::new();
    heap.pause();
    heap.resume();
    let stats = heap.force_collect();
    assert_eq!(stats.kind, Some(GcKind::Full));
}

#[test]
fn nested_pause_requires_matching_resume() {
    let heap = RcMagrGC::new();
    heap.pause();
    heap.pause();
    heap.resume();
    // Still paused after one resume
    assert_eq!(heap.force_collect().kind, None);
    heap.resume();
    // Now unpaused
    assert_eq!(heap.force_collect().kind, Some(GcKind::Full));
}

#[test]
fn collect_cycles_increments_gc_cycles() {
    let heap = RcMagrGC::new();
    heap.collect_cycles();
    heap.collect_cycles();
    assert_eq!(heap.stats().gc_cycles, 2);
}

#[test]
fn stats_collect_does_not_change_counters() {
    let heap = RcMagrGC::new();
    let _ = heap.alloc_array(vec![]);
    let before = heap.stats();
    heap.collect();  // default no-op
    assert_eq!(heap.stats(), before);
}

// ── 6. Heap config ───────────────────────────────────────────────────────────

#[test]
fn set_max_heap_bytes_reflects_in_stats() {
    let heap = RcMagrGC::new();
    heap.set_max_heap_bytes(Some(1_000_000));
    assert_eq!(heap.stats().max_bytes, Some(1_000_000));
    heap.set_max_heap_bytes(None);
    assert_eq!(heap.stats().max_bytes, None);
}

#[test]
fn used_bytes_increases_with_alloc() {
    let heap = RcMagrGC::new();
    assert_eq!(heap.used_bytes(), 0);
    let _ = heap.alloc_array(vec![Value::I64(1); 10]);
    assert!(heap.used_bytes() > 0);
}

// ── 7. Finalization (Phase 3d: real triggering) ──────────────────────────────

#[test]
fn finalizer_fires_when_object_freed_via_cycle_collect() {
    let heap = RcMagrGC::new();
    let fired = Arc::new(AtomicUsize::new(0));
    let f = fired.clone();

    let a = heap.alloc_object(dummy_type_desc("F"), vec![Value::Null], NativeData::None);
    let b = heap.alloc_object(dummy_type_desc("G"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(g) = &a else { panic!() };
        g.borrow_mut().slots[0] = b.clone();
        let Value::Object(g) = &b else { panic!() };
        g.borrow_mut().slots[0] = a.clone();
    }
    heap.register_finalizer(&a, Arc::new(move || {
        f.fetch_add(1, Ordering::SeqCst);
    }));
    drop(a);
    drop(b);

    heap.collect_cycles();

    assert_eq!(fired.load(Ordering::SeqCst), 1, "finalizer fires on cycle break");
    assert_eq!(heap.stats().finalizers_pending, 0, "finalizer removed after fire");
}

#[test]
fn finalizer_does_not_fire_when_object_kept_alive() {
    let heap = RcMagrGC::new();
    let fired = Arc::new(AtomicUsize::new(0));
    let f = fired.clone();
    let _alive = heap.alloc_object(dummy_type_desc("Alive"), vec![], NativeData::None);
    heap.register_finalizer(&_alive, Arc::new(move || {
        f.fetch_add(1, Ordering::SeqCst);
    }));

    heap.collect_cycles();

    assert_eq!(fired.load(Ordering::SeqCst), 0, "finalizer not fired for live object");
    assert_eq!(heap.stats().finalizers_pending, 1);
}

#[test]
fn finalizer_is_one_shot_after_fire() {
    let heap = RcMagrGC::new();
    let fired = Arc::new(AtomicUsize::new(0));
    let f = fired.clone();
    let a = heap.alloc_object(dummy_type_desc("F"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(g) = &a else { panic!() };
        g.borrow_mut().slots[0] = a.clone();
    }
    heap.register_finalizer(&a, Arc::new(move || {
        f.fetch_add(1, Ordering::SeqCst);
    }));
    drop(a);

    heap.collect_cycles();
    heap.collect_cycles();  // second call should not re-fire

    assert_eq!(fired.load(Ordering::SeqCst), 1, "one-shot");
}

// ── Auto-collect on memory pressure (Phase 3d) ───────────────────────────────

#[test]
fn auto_collect_triggers_when_over_threshold() {
    let heap = RcMagrGC::new();
    heap.set_max_heap_bytes(Some(2_000));  // 阈值 1800 (90%)
    let gc_before = heap.stats().gc_cycles;

    // 反复 alloc 直到 used 越过 90% 阈值
    let mut keep_alive: Vec<Value> = Vec::new();
    for _ in 0..30 {
        keep_alive.push(heap.alloc_array(vec![Value::I64(0); 8]));
    }
    let gc_after = heap.stats().gc_cycles;
    assert!(gc_after > gc_before, "auto-collect should fire when heap >90% limit");
}

#[test]
fn auto_collect_throttled_by_growth_delta() {
    let heap = RcMagrGC::new();
    heap.set_max_heap_bytes(Some(10_000));

    // 一次性 alloc 跨 90% 阈值
    let _big = heap.alloc_array(vec![Value::I64(0); 200]);
    let gc1 = heap.stats().gc_cycles;

    // 紧跟一个小 alloc：增长 < 10% (1000 bytes) 不应再触发 auto-collect
    let _small = heap.alloc_array(vec![]);
    let gc2 = heap.stats().gc_cycles;
    assert_eq!(gc1, gc2, "small alloc within throttle delta does not retrigger");
}

// ── 7-existing. Finalization counter tests (Phase 3a baseline) ───────────────

#[test]
fn register_finalizer_increments_pending_count() {
    let heap = RcMagrGC::new();
    let v = heap.alloc_array(vec![]);
    heap.register_finalizer(&v, Arc::new(|| {}));
    assert_eq!(heap.stats().finalizers_pending, 1);
}

#[test]
fn cancel_finalizer_decrements_pending_count() {
    let heap = RcMagrGC::new();
    let v = heap.alloc_array(vec![]);
    heap.register_finalizer(&v, Arc::new(|| {}));
    heap.cancel_finalizer(&v);
    assert_eq!(heap.stats().finalizers_pending, 0);
}

#[test]
fn register_finalizer_on_atomic_value_is_noop() {
    let heap = RcMagrGC::new();
    heap.register_finalizer(&Value::I64(1), Arc::new(|| {}));
    assert_eq!(heap.stats().finalizers_pending, 0);
}

// ── 8. Weak references ───────────────────────────────────────────────────────

#[test]
fn make_weak_on_object_succeeds() {
    let heap = RcMagrGC::new();
    let v = heap.alloc_object(dummy_type_desc("Foo"), vec![], NativeData::None);
    assert!(heap.make_weak(&v).is_some());
}

#[test]
fn make_weak_on_array_succeeds() {
    let heap = RcMagrGC::new();
    let v = heap.alloc_array(vec![]);
    assert!(heap.make_weak(&v).is_some());
}

#[test]
fn make_weak_on_atomic_returns_none() {
    let heap = RcMagrGC::new();
    assert!(heap.make_weak(&Value::I64(1)).is_none());
    assert!(heap.make_weak(&Value::Str("x".into())).is_none());
    assert!(heap.make_weak(&Value::Null).is_none());
    assert!(heap.make_weak(&Value::Bool(true)).is_none());
}

#[test]
fn upgrade_weak_succeeds_while_strong_alive() {
    let heap = RcMagrGC::new();
    let v = heap.alloc_array(vec![Value::I64(42)]);
    let w = heap.make_weak(&v).unwrap();
    let upgraded = heap.upgrade_weak(&w).unwrap();
    let (Value::Array(a), Value::Array(b)) = (&v, &upgraded) else { panic!() };
    assert!(crate::gc::GcRef::ptr_eq(a, b));
}

#[test]
fn upgrade_weak_fails_after_strong_dropped() {
    let heap = RcMagrGC::new();
    let w = {
        let v = heap.alloc_array(vec![]);
        heap.make_weak(&v).unwrap()
    }; // v 在此处 drop
    assert!(heap.upgrade_weak(&w).is_none());
}

// ── 9. Event observers ───────────────────────────────────────────────────────

#[derive(Debug, Default)]
struct CountingObserver {
    count: AtomicUsize,
}
impl GcObserver for CountingObserver {
    fn on_event(&self, _event: &GcEvent) {
        self.count.fetch_add(1, Ordering::SeqCst);
    }
}

#[test]
fn add_observer_increments_count() {
    let heap = RcMagrGC::new();
    heap.add_observer(Arc::new(CountingObserver::default()));
    assert_eq!(heap.stats().observers, 1);
}

#[test]
fn observer_receives_before_and_after_on_collect_cycles() {
    let heap = RcMagrGC::new();
    let obs  = Arc::new(CountingObserver::default());
    heap.add_observer(obs.clone());
    heap.collect_cycles();
    // 2 events: BeforeCollect + AfterCollect
    assert_eq!(obs.count.load(Ordering::SeqCst), 2);
}

#[test]
fn observer_receives_before_and_after_on_force_collect() {
    let heap = RcMagrGC::new();
    let obs  = Arc::new(CountingObserver::default());
    heap.add_observer(obs.clone());
    heap.force_collect();
    assert_eq!(obs.count.load(Ordering::SeqCst), 2);
}

#[test]
fn remove_observer_stops_event_delivery() {
    let heap = RcMagrGC::new();
    let obs  = Arc::new(CountingObserver::default());
    let id = heap.add_observer(obs.clone());
    heap.remove_observer(id);
    heap.collect_cycles();
    assert_eq!(obs.count.load(Ordering::SeqCst), 0);
    assert_eq!(heap.stats().observers, 0);
}

#[derive(Debug, Default)]
struct OomObserver {
    oom_seen: AtomicUsize,
}
impl GcObserver for OomObserver {
    fn on_event(&self, event: &GcEvent) {
        if matches!(event, GcEvent::OutOfMemory { .. }) {
            self.oom_seen.fetch_add(1, Ordering::SeqCst);
        }
    }
}

#[test]
fn oom_event_fires_when_used_exceeds_limit() {
    let heap = RcMagrGC::new();
    let obs  = Arc::new(OomObserver::default());
    heap.add_observer(obs.clone());
    heap.set_max_heap_bytes(Some(8));  // tiny limit
    let _ = heap.alloc_array(vec![Value::I64(0); 100]);
    assert!(obs.oom_seen.load(Ordering::SeqCst) >= 1);
}

// ── 10. Profiler ─────────────────────────────────────────────────────────────

#[test]
fn alloc_sampler_fires_for_each_alloc() {
    let heap = RcMagrGC::new();
    let counter = Arc::new(AtomicUsize::new(0));
    let c = counter.clone();
    heap.set_alloc_sampler(Some(Arc::new(move |_sample| {
        c.fetch_add(1, Ordering::SeqCst);
    })));
    let _ = heap.alloc_array(vec![]);
    let _ = heap.alloc_array(vec![Value::I64(1)]);
    let _ = heap.alloc_object(dummy_type_desc("Foo"), vec![], NativeData::None);
    assert_eq!(counter.load(Ordering::SeqCst), 3);
}

#[test]
fn set_alloc_sampler_none_clears_sampler() {
    let heap = RcMagrGC::new();
    let counter = Arc::new(AtomicUsize::new(0));
    let c = counter.clone();
    heap.set_alloc_sampler(Some(Arc::new(move |_| {
        c.fetch_add(1, Ordering::SeqCst);
    })));
    heap.set_alloc_sampler(None);
    let _ = heap.alloc_array(vec![]);
    assert_eq!(counter.load(Ordering::SeqCst), 0);
}

#[test]
fn snapshot_coverage_is_full_after_phase_3b_registry() {
    // Phase 3b: heap registry 升级 coverage 到 Full（不依赖 host pin）。
    let heap = RcMagrGC::new();
    let snap = heap.take_snapshot();
    assert_eq!(snap.coverage, SnapshotCoverage::Full);
}

#[test]
fn snapshot_includes_unpinned_alive_object() {
    // Phase 3b: 没 pin 也能在 snapshot 里看到（只要还存活）。
    let heap = RcMagrGC::new();
    let _alive = heap.alloc_object(dummy_type_desc("UnpinnedFoo"), vec![], NativeData::None);
    let snap = heap.take_snapshot();
    assert!(snap.objects_by_type.contains_key("UnpinnedFoo"));
    assert!(snap.total_objects >= 1);
}

#[test]
fn iterate_live_objects_full_coverage_includes_unpinned() {
    // Phase 3b: alloc 但没 pin 的对象，iterate_live_objects 也能找到。
    let heap = RcMagrGC::new();
    let _a = heap.alloc_array(vec![]);
    let _b = heap.alloc_object(dummy_type_desc("Foo"), vec![], NativeData::None);
    let mut count = 0;
    heap.iterate_live_objects(&mut |_| count += 1);
    assert_eq!(count, 2);
}

// ── Interp stack scanning (Phase 3f) ─────────────────────────────────────────

#[test]
fn frame_held_outer_with_inner_chain_protected_by_stack_scan() {
    // Phase 3f bug 场景：frame.regs 持 outer，outer 通过 slot 间接持 inner
    // （inner 没在 reg 里）。如果 scanner 不扫 frame regs，outer 在 unreachable
    // 集里，inner 通过 outer.slot 引用被算 internal 减除 → tentative=0 → inner
    // 被错误清空。Phase 3f 把 frame regs 暴露给 scanner 修复此问题。
    //
    // 本测试用 set_external_root_scanner 直接模拟：把"frame regs Vec" 通过
    // Rc<RefCell> 共享给 scanner 闭包（与 vm_context.rs 中实际 raw-ptr 实现等价）。
    let heap = RcMagrGC::new();

    let inner = heap.alloc_object(dummy_type_desc("Inner"), vec![Value::I64(42)], NativeData::None);
    let outer = heap.alloc_object(dummy_type_desc("Outer"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(g) = &outer else { panic!() };
        g.borrow_mut().slots[0] = inner.clone();
    }

    let frame_regs: std::rc::Rc<std::cell::RefCell<Vec<Value>>>
        = std::rc::Rc::new(std::cell::RefCell::new(vec![outer.clone()]));

    {
        let fr = frame_regs.clone();
        heap.set_external_root_scanner(Box::new(move |visit| {
            for v in fr.borrow().iter() {
                visit(v);
            }
        }));
    }

    drop(outer);
    drop(inner);

    heap.collect_cycles();

    // Verify: outer / inner 数据 intact（inner.slots[0] = 42 未被错误清空）
    let regs = frame_regs.borrow();
    let Value::Object(outer_gc) = &regs[0] else { panic!() };
    let inner_val = outer_gc.borrow().slots[0].clone();
    let Value::Object(inner_gc) = &inner_val else {
        panic!("inner should still be Object, got {:?}", inner_val);
    };
    assert_eq!(inner_gc.borrow().slots[0], Value::I64(42),
        "Phase 3f: transitively reachable inner survives collect when outer in frame regs");
}

// ── External root scanner (Phase 3d.1) ───────────────────────────────────────

#[test]
fn external_root_scanner_called_during_collect() {
    let heap = RcMagrGC::new();
    let calls = Arc::new(AtomicUsize::new(0));
    let c = calls.clone();
    heap.set_external_root_scanner(Box::new(move |_visit| {
        c.fetch_add(1, Ordering::SeqCst);
    }));

    heap.collect_cycles();
    assert!(calls.load(Ordering::SeqCst) >= 1, "scanner invoked during mark phase");
}

#[test]
fn cycle_reachable_via_external_scanner_is_preserved() {
    use std::cell::RefCell as StdRefCell;
    use std::rc::Rc as StdRc;

    let heap = RcMagrGC::new();

    // 模拟 VmContext.static_fields 风格的外部容器
    let external: StdRc<StdRefCell<Vec<Value>>> = StdRc::new(StdRefCell::new(Vec::new()));

    // scanner 把 external 中所有 Value 暴露为 roots
    {
        let ext = external.clone();
        heap.set_external_root_scanner(Box::new(move |visit| {
            for v in ext.borrow().iter() {
                visit(v);
            }
        }));
    }

    // 构造一个 a-b 环
    let a = heap.alloc_object(dummy_type_desc("A"), vec![Value::Null], NativeData::None);
    let b = heap.alloc_object(dummy_type_desc("B"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(g) = &a else { panic!() };
        g.borrow_mut().slots[0] = b.clone();
        let Value::Object(g) = &b else { panic!() };
        g.borrow_mut().slots[0] = a.clone();
    }

    // 把 a 放到 external（模拟 static_fields 持有），drop 本地强引用
    external.borrow_mut().push(a.clone());
    drop(a);
    drop(b);

    heap.collect_cycles();

    // a 应该仍然存活（external scanner 把它喂进 reachable）
    // b 也存活（a.slots[0] = b 链可达）
    let mut count = 0;
    heap.iterate_live_objects(&mut |_| count += 1);
    assert_eq!(count, 2, "external-rooted cycle survives collect");

    // 关键：a 的 slots 应该 intact（不被误清）
    let alive_a = external.borrow()[0].clone();
    let Value::Object(g) = &alive_a else { panic!() };
    let slot0 = g.borrow().slots[0].clone();
    assert!(matches!(slot0, Value::Object(_)),
        "a.slots[0] still references b (NOT cleared by collector)");
}

#[test]
fn cycle_unreachable_from_external_scanner_still_collected() {
    use std::cell::RefCell as StdRefCell;
    use std::rc::Rc as StdRc;

    let heap = RcMagrGC::new();
    let external: StdRc<StdRefCell<Vec<Value>>> = StdRc::new(StdRefCell::new(Vec::new()));
    {
        let ext = external.clone();
        heap.set_external_root_scanner(Box::new(move |visit| {
            for v in ext.borrow().iter() { visit(v); }
        }));
    }

    // 构造 a-b 环但**不**把 a 放进 external
    let a = heap.alloc_object(dummy_type_desc("A"), vec![Value::Null], NativeData::None);
    let b = heap.alloc_object(dummy_type_desc("B"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(g) = &a else { panic!() };
        g.borrow_mut().slots[0] = b.clone();
        let Value::Object(g) = &b else { panic!() };
        g.borrow_mut().slots[0] = a.clone();
    }
    drop(a); drop(b);

    heap.collect_cycles();

    // 没在 external 里 → 仍是 unreachable → 被收集
    let mut count = 0;
    heap.iterate_live_objects(&mut |_| count += 1);
    assert_eq!(count, 0, "non-rooted cycle still collected");
}

// ── Cycle collection (Phase 3c) ──────────────────────────────────────────────

fn alive_count(heap: &RcMagrGC) -> usize {
    let mut n = 0;
    heap.iterate_live_objects(&mut |_| n += 1);
    n
}

#[test]
fn simple_two_node_cycle_is_freed_after_collect() {
    let heap = RcMagrGC::new();
    let a = heap.alloc_object(dummy_type_desc("A"), vec![Value::Null], NativeData::None);
    let b = heap.alloc_object(dummy_type_desc("B"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(a_gc) = &a else { panic!() };
        let Value::Object(b_gc) = &b else { panic!() };
        a_gc.borrow_mut().slots[0] = b.clone();
        b_gc.borrow_mut().slots[0] = a.clone();
    }
    drop(a);
    drop(b);
    assert_eq!(alive_count(&heap), 2, "cycle keeps both alive before collect");

    heap.collect_cycles();

    assert_eq!(alive_count(&heap), 0, "cycle collector frees both nodes");
}

#[test]
fn self_reference_cycle_is_freed() {
    let heap = RcMagrGC::new();
    let a = heap.alloc_object(dummy_type_desc("Self"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(a_gc) = &a else { panic!() };
        a_gc.borrow_mut().slots[0] = a.clone();
    }
    drop(a);
    assert_eq!(alive_count(&heap), 1);

    heap.collect_cycles();

    assert_eq!(alive_count(&heap), 0, "self-reference cycle freed");
}

#[test]
fn cycle_with_external_user_ref_is_not_broken_yet() {
    let heap = RcMagrGC::new();
    let a = heap.alloc_object(dummy_type_desc("A"), vec![Value::Null], NativeData::None);
    let b = heap.alloc_object(dummy_type_desc("B"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(a_gc) = &a else { panic!() };
        let Value::Object(b_gc) = &b else { panic!() };
        a_gc.borrow_mut().slots[0] = b.clone();
        b_gc.borrow_mut().slots[0] = a.clone();
    }
    let user_a = a.clone();
    drop(a);
    drop(b);

    heap.collect_cycles();

    // 用户外部还持 a，所以 a 不被断（tentative > 0）；b 被断后仍由 a.slots[0] 持有 → 仍存活
    assert_eq!(alive_count(&heap), 2, "external user ref keeps both alive");

    // 用户释放后第二次 collect（实际上不需要 collect，普通 Drop 链就够了）
    drop(user_a);
    // 此时 a.count → 0 → drop a → drop a.slots[0] 即 b 的 Rc → b.count → 0 → drop b
    assert_eq!(alive_count(&heap), 0, "after user drops, RC drop chain finishes the release");
}

#[test]
fn pinned_root_cycle_is_not_broken() {
    let heap = RcMagrGC::new();
    let a = heap.alloc_object(dummy_type_desc("A"), vec![Value::Null], NativeData::None);
    let b = heap.alloc_object(dummy_type_desc("B"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(a_gc) = &a else { panic!() };
        let Value::Object(b_gc) = &b else { panic!() };
        a_gc.borrow_mut().slots[0] = b.clone();
        b_gc.borrow_mut().slots[0] = a.clone();
    }
    let _root = heap.pin_root(a.clone());

    heap.collect_cycles();

    // a / b 都是 reachable from pinned root → 不被破坏
    assert_eq!(alive_count(&heap), 2, "pinned cycle survives collect");
    let Value::Object(a_gc) = &a else { panic!() };
    // a.slots[0] 还是 b 的引用，不是 Null
    assert!(matches!(a_gc.borrow().slots[0], Value::Object(_)));
}

#[test]
fn unrelated_alive_object_is_not_affected_by_collect() {
    let heap = RcMagrGC::new();
    let _alive = heap.alloc_object(dummy_type_desc("Alive"), vec![Value::I64(42)], NativeData::None);
    // 不构造环；_alive 由当前作用域强引用持有

    heap.collect_cycles();

    assert_eq!(alive_count(&heap), 1, "non-cycle object not affected");
    let Value::Object(gc) = &_alive else { panic!() };
    assert_eq!(gc.borrow().slots[0], Value::I64(42), "data intact");
}

#[test]
fn multiple_disjoint_cycles_all_freed() {
    let heap = RcMagrGC::new();
    // 第一个环 a-b
    let a = heap.alloc_object(dummy_type_desc("A1"), vec![Value::Null], NativeData::None);
    let b = heap.alloc_object(dummy_type_desc("B1"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(g) = &a else { panic!() };
        g.borrow_mut().slots[0] = b.clone();
        let Value::Object(g) = &b else { panic!() };
        g.borrow_mut().slots[0] = a.clone();
    }
    // 第二个环 c-d
    let c = heap.alloc_object(dummy_type_desc("C2"), vec![Value::Null], NativeData::None);
    let d = heap.alloc_object(dummy_type_desc("D2"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(g) = &c else { panic!() };
        g.borrow_mut().slots[0] = d.clone();
        let Value::Object(g) = &d else { panic!() };
        g.borrow_mut().slots[0] = c.clone();
    }
    drop(a); drop(b); drop(c); drop(d);
    assert_eq!(alive_count(&heap), 4);

    heap.collect_cycles();

    assert_eq!(alive_count(&heap), 0, "both cycles independently freed");
}

#[test]
fn collect_cycles_freed_bytes_observable() {
    let heap = RcMagrGC::new();
    let a = heap.alloc_object(dummy_type_desc("A"), vec![Value::Null], NativeData::None);
    let b = heap.alloc_object(dummy_type_desc("B"), vec![Value::Null], NativeData::None);
    {
        let Value::Object(g) = &a else { panic!() };
        g.borrow_mut().slots[0] = b.clone();
        let Value::Object(g) = &b else { panic!() };
        g.borrow_mut().slots[0] = a.clone();
    }
    drop(a);
    drop(b);

    let used_before = heap.used_bytes();
    let stats = heap.force_collect();
    assert_eq!(stats.kind, Some(GcKind::Full));
    assert!(stats.freed_bytes > 0, "force_collect should report freed bytes for cycle");
    let used_after = heap.used_bytes();
    assert!(used_after < used_before, "used_bytes decreases after cycle collection");
}

#[test]
fn registry_prunes_dropped_objects() {
    // Phase 3b: 对象 drop 后 registry 自动清掉（下次 iterate / snapshot 时 prune）。
    let heap = RcMagrGC::new();
    {
        let _ephemeral = heap.alloc_array(vec![]);
        // _ephemeral 出 scope drop
    }
    let mut count = 0;
    heap.iterate_live_objects(&mut |_| count += 1);
    assert_eq!(count, 0);
}

#[test]
fn snapshot_includes_pinned_root_object() {
    let heap = RcMagrGC::new();
    let v = heap.alloc_object(dummy_type_desc("Foo"), vec![], NativeData::None);
    let _h = heap.pin_root(v);
    let snap = heap.take_snapshot();
    assert!(snap.total_objects >= 1);
    assert!(snap.objects_by_type.contains_key("Foo"));
}

#[test]
fn snapshot_traverses_nested_objects() {
    let heap = RcMagrGC::new();
    let inner1 = heap.alloc_object(dummy_type_desc("Inner"), vec![], NativeData::None);
    let inner2 = heap.alloc_object(dummy_type_desc("Inner"), vec![], NativeData::None);
    let outer  = heap.alloc_object(
        dummy_type_desc("Outer"),
        vec![inner1, inner2],
        NativeData::None,
    );
    let _h = heap.pin_root(outer);
    let snap = heap.take_snapshot();
    assert!(snap.total_objects >= 3);
    assert_eq!(snap.objects_by_type.get("Outer").map(|s| s.count), Some(1));
    assert_eq!(snap.objects_by_type.get("Inner").map(|s| s.count), Some(2));
}

#[test]
fn iterate_live_objects_visits_root_reachable() {
    let heap = RcMagrGC::new();
    let elems = (0..5)
        .map(|i| heap.alloc_object(dummy_type_desc("Foo"), vec![Value::I64(i)], NativeData::None))
        .collect::<Vec<_>>();
    let arr = heap.alloc_array(elems);
    let _h  = heap.pin_root(arr);

    let mut count = 0;
    heap.iterate_live_objects(&mut |_v| count += 1);
    // 1 array + 5 objects
    assert_eq!(count, 6);
}

#[test]
fn iterate_live_objects_dedupes_cycle() {
    let heap = RcMagrGC::new();
    // 自引用 cycle：obj.slots[0] = obj 自己（通过 wrap-by-clone）
    let obj = heap.alloc_object(dummy_type_desc("Cycle"), vec![Value::Null], NativeData::None);
    let Value::Object(rc) = &obj else { panic!() };
    rc.borrow_mut().slots[0] = obj.clone();
    let _h = heap.pin_root(obj);

    let mut count = 0;
    heap.iterate_live_objects(&mut |_v| count += 1);
    // visited dedup → 仅 1 次
    assert_eq!(count, 1);
}

// ── 11. Stats ────────────────────────────────────────────────────────────────

#[test]
fn stats_allocations_monotonically_increases() {
    let heap = RcMagrGC::new();
    assert_eq!(heap.stats().allocations, 0);
    let _ = heap.alloc_array(vec![]);
    assert_eq!(heap.stats().allocations, 1);
    let _ = heap.alloc_array(vec![Value::I64(1)]);
    let _ = heap.alloc_object(dummy_type_desc("Foo"), vec![], NativeData::None);
    assert_eq!(heap.stats().allocations, 3);
}

#[test]
fn stats_gc_cycles_increments_on_collect_cycles() {
    let heap = RcMagrGC::new();
    assert_eq!(heap.stats().gc_cycles, 0);
    heap.collect_cycles();
    assert_eq!(heap.stats().gc_cycles, 1);
    heap.collect_cycles();
    heap.collect_cycles();
    assert_eq!(heap.stats().gc_cycles, 3);
}

#[test]
fn stats_struct_has_all_expected_fields() {
    let heap = RcMagrGC::new();
    let s = heap.stats();
    // Just access every field — compile time check that all 7 fields exist
    let _ = (
        s.allocations,
        s.gc_cycles,
        s.used_bytes,
        s.max_bytes,
        s.roots_pinned,
        s.finalizers_pending,
        s.observers,
    );
}
