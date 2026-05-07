use super::*;

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

