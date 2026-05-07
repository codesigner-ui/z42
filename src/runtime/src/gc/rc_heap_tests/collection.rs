use super::*;

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

