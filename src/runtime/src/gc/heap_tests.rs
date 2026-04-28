//! `MagrGC` trait 默认方法契约测试。
//!
//! 验证 trait 的默认实现（Phase 1 全部 no-op）行为符合预期 ——
//! 不 panic、不改变可观察状态。具体后端的行为测试在 `rc_heap_tests.rs`。

use crate::gc::{HeapStats, MagrGC, RcMagrGC};
use crate::metadata::Value;

#[test]
fn default_write_barrier_is_noop() {
    let heap = RcMagrGC::new();
    let owner = heap.alloc_array(vec![Value::I64(1)]);
    let new   = Value::I64(42);
    let stats_before = heap.stats();
    heap.write_barrier(&owner, 0, &new);
    // Phase 1: 默认实现 no-op，不应改变 allocations / gc_cycles。
    assert_eq!(heap.stats().allocations, stats_before.allocations);
    assert_eq!(heap.stats().gc_cycles,   stats_before.gc_cycles);
}

#[test]
fn default_collect_is_noop() {
    let heap = RcMagrGC::new();
    let _    = heap.alloc_array(vec![]);
    let stats_before = heap.stats();
    heap.collect();
    assert_eq!(heap.stats().allocations, stats_before.allocations);
    assert_eq!(heap.stats().gc_cycles,   stats_before.gc_cycles);
}

#[test]
fn heap_stats_default_is_zero() {
    let s = HeapStats::default();
    assert_eq!(s.allocations, 0);
    assert_eq!(s.gc_cycles,   0);
}
