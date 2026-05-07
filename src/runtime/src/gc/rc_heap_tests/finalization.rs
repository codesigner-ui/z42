use super::*;

// ── 7-1. Finalization (Phase 3e: drop-time triggering) ───────────────────────

#[test]
fn finalizer_fires_on_normal_rc_drop_no_cycle_no_collect() {
    // Phase 3e 关键测试：alloc + register finalizer + drop 最后一个引用 →
    // GcAllocation::Drop 自动触发 finalizer（无需调用 collect_cycles）。
    let heap = RcMagrGC::new();
    let fired = Arc::new(AtomicUsize::new(0));
    let f = fired.clone();

    {
        let v = heap.alloc_object(dummy_type_desc("Drop"), vec![], NativeData::None);
        heap.register_finalizer(&v, Arc::new(move || {
            f.fetch_add(1, Ordering::SeqCst);
        }));
        // v 出 scope，最后一个强引用消失 → GcAllocation::Drop → finalizer 自动触发
    }

    assert_eq!(fired.load(Ordering::SeqCst), 1, "finalizer fires on plain Rc drop");
}

#[test]
fn finalizer_one_shot_via_drop_then_collect() {
    // Phase 3e: drop 触发 finalizer 后，registry 已无该对象；collect_cycles
    // 找不到也不会重发。
    let heap = RcMagrGC::new();
    let fired = Arc::new(AtomicUsize::new(0));
    let f = fired.clone();

    {
        let v = heap.alloc_object(dummy_type_desc("OneShot"), vec![], NativeData::None);
        heap.register_finalizer(&v, Arc::new(move || {
            f.fetch_add(1, Ordering::SeqCst);
        }));
    } // drop → fire

    heap.collect_cycles();
    heap.collect_cycles();

    assert_eq!(fired.load(Ordering::SeqCst), 1, "one-shot via Drop");
}

#[test]
fn finalizers_pending_reflects_alive_with_finalizer() {
    // Phase 3e: stats.finalizers_pending 由 snapshot 重算，反映当前
    // 仍存活且有 finalizer 的对象数。
    let heap = RcMagrGC::new();
    let v1 = heap.alloc_object(dummy_type_desc("F1"), vec![], NativeData::None);
    let v2 = heap.alloc_object(dummy_type_desc("F2"), vec![], NativeData::None);
    let _v3 = heap.alloc_object(dummy_type_desc("F3"), vec![], NativeData::None);
    heap.register_finalizer(&v1, Arc::new(|| {}));
    heap.register_finalizer(&v2, Arc::new(|| {}));
    // _v3 没 finalizer
    assert_eq!(heap.stats().finalizers_pending, 2);

    heap.cancel_finalizer(&v1);
    assert_eq!(heap.stats().finalizers_pending, 1);

    drop(v2);  // drop → finalizer fires + obj gone → 计数减
    assert_eq!(heap.stats().finalizers_pending, 0);
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

