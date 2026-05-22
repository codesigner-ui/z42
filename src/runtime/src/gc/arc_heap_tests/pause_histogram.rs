//! Integration tests for [`PauseHistogram`] on `ArcMagrGC`
//! (add-gc-pause-histogram, 2026-05-22).
//!
//! Per-field bucket / boundary correctness is covered in
//! `gc::types::types_tests`; here we verify the heap actually wires
//! `record(pause_us)` into every collect path and projects the
//! histogram through `stats()` correctly.

use super::*;
use crate::gc::GcMode;

fn alloc_obj(heap: &ArcMagrGC, name: &str) -> Value {
    heap.alloc_object(dummy_type_desc(name), vec![Value::Null], NativeData::None)
}

#[test]
fn empty_heap_reports_default_histogram() {
    let heap = ArcMagrGC::default();
    let h = heap.stats().pause_histogram;
    assert_eq!(h.count, 0);
    assert_eq!(h.buckets, [0; 8]);
    assert_eq!(h.min_us, u64::MAX, "sentinel preserved when count == 0");
}

#[test]
fn histogram_visible_in_stats_after_collect() {
    let heap = ArcMagrGC::default();
    heap.set_mode(GcMode::StwMarkSweep);

    // A handful of force_collect calls (no live allocations needed —
    // we just want each collect to record into the histogram).
    let n = 5;
    for _ in 0..n {
        let _ = heap.force_collect();
    }

    let h = heap.stats().pause_histogram;
    assert_eq!(h.count, n as u64, "every collect must record");

    // Sum across all buckets should match count exactly.
    let bucket_sum: u64 = h.buckets.iter().sum();
    assert_eq!(bucket_sum, h.count);

    // total_us is the sum of every recorded pause; must be ≥ count*0
    // (trivially true) and the empty-sentinel min must have been
    // replaced by a real measurement.
    assert!(h.min_us != u64::MAX, "first record clears sentinel");
    assert!(h.max_us >= h.min_us);
}

#[test]
fn histogram_persists_across_mode_switches() {
    let heap = ArcMagrGC::default();

    heap.set_mode(GcMode::StwMarkSweep);
    let _ = heap.force_collect();
    let _ = heap.force_collect();
    let count_after_stw = heap.stats().pause_histogram.count;
    assert_eq!(count_after_stw, 2);

    // Switch mode — histogram must NOT reset.
    heap.set_mode(GcMode::GenerationalMarkSweep);
    let count_after_switch = heap.stats().pause_histogram.count;
    assert_eq!(count_after_switch, 2, "mode switch preserves histogram");

    let _ = heap.force_collect();
    let count_after_more = heap.stats().pause_histogram.count;
    assert_eq!(count_after_more, 3);
}

#[test]
fn histogram_records_force_collect() {
    // force_collect uses GcKind::Full path, which is separate from
    // the cycle-collector path; make sure both wire `record`.
    let heap = ArcMagrGC::default();
    heap.set_mode(GcMode::StwMarkSweep);

    let _v = alloc_obj(&heap, "Foo");
    let _ = heap.force_collect();

    let h = heap.stats().pause_histogram;
    assert_eq!(h.count, 1);
    assert!(h.total_us >= h.min_us);
}
