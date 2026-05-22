//! add-concurrent-gc tests — covers P2 helpers (`mark_if_unmarked`,
//! `snapshot_roots_into_mark_queue`) and grows in P3 (barrier override),
//! P4 (concurrent mark loop), P5 (multi-thread stress).
//!
//! P2 tests verify the queue plumbing in isolation: roots get marked +
//! enqueued, idempotency on re-snapshot, primitive roots ignored.

use super::*;
use crate::gc::{GcMode, GcRef, MagrGC};

#[test]
fn snapshot_roots_marks_and_enqueues_pinned_roots() {
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::ConcurrentMarkSweep);

    let obj = heap.alloc_object(dummy_type_desc("Root"), vec![Value::Null], NativeData::None);
    let _pin = heap.pin_root(obj.clone());

    let count = heap.snapshot_roots_into_mark_queue_for_test();
    assert_eq!(count, 1, "single pinned root → 1 newly-marked");

    let queue = heap.mark_queue_for_test();
    assert_eq!(queue.len(), 1);
    let Value::Object(rc) = &obj else { panic!() };
    assert!(GcRef::is_marked(rc), "root is marked after snapshot");

    // Reset so other tests aren't affected.
    GcRef::clear_mark(rc);
}

#[test]
fn snapshot_roots_idempotent_on_already_marked() {
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::ConcurrentMarkSweep);

    let obj = heap.alloc_object(dummy_type_desc("R"), vec![Value::Null], NativeData::None);
    let _pin = heap.pin_root(obj.clone());

    let first = heap.snapshot_roots_into_mark_queue_for_test();
    assert_eq!(first, 1);

    // Second snapshot without clearing marks → 0 new marks; queue is
    // re-cleared then re-populated (only newly-marked enter).
    let second = heap.snapshot_roots_into_mark_queue_for_test();
    assert_eq!(second, 0, "already-marked roots not counted again");
    assert_eq!(heap.mark_queue_for_test().len(), 0,
        "queue empty when no new roots marked");

    let Value::Object(rc) = &obj else { panic!() };
    GcRef::clear_mark(rc);
}

#[test]
fn snapshot_roots_skips_primitive_roots() {
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::ConcurrentMarkSweep);

    // Pin a primitive root (legitimate but mark-irrelevant).
    let _pin = heap.pin_root(Value::I64(42));

    let count = heap.snapshot_roots_into_mark_queue_for_test();
    assert_eq!(count, 0, "primitive root → 0 marks");
    assert_eq!(heap.mark_queue_for_test().len(), 0);
}

#[test]
fn snapshot_roots_includes_external_scanner_output() {
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::ConcurrentMarkSweep);

    let external_obj = heap.alloc_object(
        dummy_type_desc("External"), vec![Value::Null], NativeData::None
    );
    let external_clone = external_obj.clone();
    heap.set_external_root_scanner(Box::new(move |visit| {
        visit(&external_clone);
    }));

    let count = heap.snapshot_roots_into_mark_queue_for_test();
    assert_eq!(count, 1, "external scanner yielded 1 root → 1 mark");

    let Value::Object(rc) = &external_obj else { panic!() };
    assert!(GcRef::is_marked(rc));
    GcRef::clear_mark(rc);
}

#[test]
fn mark_if_unmarked_returns_true_first_then_false() {
    let heap = ArcMagrGC::new();
    let obj = heap.alloc_object(dummy_type_desc("X"), vec![], NativeData::None);

    assert!(ArcMagrGC::mark_if_unmarked_for_test(&obj), "first call marks");
    assert!(!ArcMagrGC::mark_if_unmarked_for_test(&obj), "second call CAS fails");

    let Value::Object(rc) = &obj else { panic!() };
    assert!(GcRef::is_marked(rc));
    GcRef::clear_mark(rc);
}

#[test]
fn mark_if_unmarked_returns_false_for_primitives() {
    assert!(!ArcMagrGC::mark_if_unmarked_for_test(&Value::I64(1)));
    assert!(!ArcMagrGC::mark_if_unmarked_for_test(&Value::Null));
    assert!(!ArcMagrGC::mark_if_unmarked_for_test(&Value::Bool(true)));
    assert!(!ArcMagrGC::mark_if_unmarked_for_test(&Value::Str("x".into())));
}

#[test]
fn mark_queue_starts_empty_in_default_heap() {
    let heap = ArcMagrGC::new();
    assert!(heap.mark_queue_for_test().is_empty(),
        "fresh heap has empty mark_queue regardless of mode");
}

// ── P3: Barrier override branches on mode ──────────────────────────────────

#[test]
fn barrier_field_no_op_in_stw_mode() {
    let heap = ArcMagrGC::new();
    assert_eq!(heap.mode(), GcMode::StwMarkSweep);

    let owner = heap.alloc_object(dummy_type_desc("O"), vec![Value::Null], NativeData::None);
    let new = heap.alloc_object(dummy_type_desc("N"), vec![], NativeData::None);

    heap.write_barrier_field(&owner, 0, &new);

    assert!(heap.mark_queue_for_test().is_empty(),
        "STW mode → barrier is no-op, mark_queue stays empty");
    let Value::Object(rc) = &new else { panic!() };
    assert!(!GcRef::is_marked(rc),
        "STW mode → barrier does not mark new value");
}

#[test]
fn barrier_field_shades_new_value_in_concurrent_mode() {
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::ConcurrentMarkSweep);

    let owner = heap.alloc_object(dummy_type_desc("O"), vec![Value::Null], NativeData::None);
    let new = heap.alloc_object(dummy_type_desc("N"), vec![], NativeData::None);

    heap.write_barrier_field(&owner, 0, &new);

    let queue = heap.mark_queue_for_test();
    assert_eq!(queue.len(), 1, "concurrent mode → barrier pushes new to mark_queue");
    let Value::Object(rc) = &new else { panic!() };
    assert!(GcRef::is_marked(rc),
        "concurrent mode → barrier marks new value gray");

    GcRef::clear_mark(rc);
}

#[test]
fn barrier_array_shades_new_value_in_concurrent_mode() {
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::ConcurrentMarkSweep);

    let arr = heap.alloc_array(vec![Value::Null]);
    let new = heap.alloc_object(dummy_type_desc("E"), vec![], NativeData::None);

    heap.write_barrier_array_elem(&arr, 0, &new);

    let queue = heap.mark_queue_for_test();
    assert_eq!(queue.len(), 1);
    let Value::Object(rc) = &new else { panic!() };
    assert!(GcRef::is_marked(rc));
    GcRef::clear_mark(rc);
}

#[test]
fn barrier_idempotent_on_already_marked_in_concurrent_mode() {
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::ConcurrentMarkSweep);

    let owner = heap.alloc_object(dummy_type_desc("O"), vec![Value::Null], NativeData::None);
    let new = heap.alloc_object(dummy_type_desc("N"), vec![], NativeData::None);

    heap.write_barrier_field(&owner, 0, &new);
    heap.write_barrier_field(&owner, 0, &new);  // duplicate write

    let queue = heap.mark_queue_for_test();
    assert_eq!(queue.len(), 1,
        "duplicate write → CAS fails second time → no re-enqueue");

    let Value::Object(rc) = &new else { panic!() };
    GcRef::clear_mark(rc);
}

#[test]
fn barrier_mode_switch_takes_effect_immediately_on_next_write() {
    let heap = ArcMagrGC::new();
    let owner = heap.alloc_object(dummy_type_desc("O"), vec![Value::Null], NativeData::None);
    let n1 = heap.alloc_object(dummy_type_desc("N1"), vec![], NativeData::None);
    let n2 = heap.alloc_object(dummy_type_desc("N2"), vec![], NativeData::None);

    // STW mode: barrier no-op
    heap.write_barrier_field(&owner, 0, &n1);
    assert!(heap.mark_queue_for_test().is_empty());

    // Switch to concurrent: next barrier shades
    heap.set_mode(GcMode::ConcurrentMarkSweep);
    heap.write_barrier_field(&owner, 0, &n2);

    let queue = heap.mark_queue_for_test();
    assert_eq!(queue.len(), 1, "switch effective on next write");

    let Value::Object(rc) = &n2 else { panic!() };
    GcRef::clear_mark(rc);
}
