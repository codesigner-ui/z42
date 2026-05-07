use super::*;

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

