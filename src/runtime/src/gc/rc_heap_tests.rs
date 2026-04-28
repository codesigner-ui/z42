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

// ── 7. Finalization ──────────────────────────────────────────────────────────

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
fn snapshot_coverage_is_reachable_from_pinned_roots_in_rc_mode() {
    let heap = RcMagrGC::new();
    let snap = heap.take_snapshot();
    assert_eq!(snap.coverage, SnapshotCoverage::ReachableFromPinnedRoots);
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
