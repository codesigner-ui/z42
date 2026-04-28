//! `RcMagrGC` 单元测试。

use std::collections::HashMap;
use std::sync::Arc;

use crate::gc::{MagrGC, RcMagrGC};
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

// ── alloc_object ──────────────────────────────────────────────────────────────

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
    assert!(!std::rc::Rc::ptr_eq(&ra, &rb));
}

// ── alloc_array ──────────────────────────────────────────────────────────────

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

// ── alloc_map ────────────────────────────────────────────────────────────────

#[test]
fn alloc_map_returns_empty_value_map() {
    let heap = RcMagrGC::new();
    let v    = heap.alloc_map();
    let Value::Map(rc) = v else { panic!("expected Value::Map") };
    assert!(rc.borrow().is_empty());
}

// ── stats counters ────────────────────────────────────────────────────────────

#[test]
fn stats_allocations_monotonically_increases() {
    let heap = RcMagrGC::new();
    assert_eq!(heap.stats().allocations, 0);
    let _ = heap.alloc_array(vec![]);
    assert_eq!(heap.stats().allocations, 1);
    let _ = heap.alloc_array(vec![Value::I64(1)]);
    let _ = heap.alloc_map();
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
fn stats_collect_does_not_change_counters() {
    let heap = RcMagrGC::new();
    let _ = heap.alloc_array(vec![]);
    let before = heap.stats();
    heap.collect();
    // Phase 1: collect 是 no-op，不改 stats（只有 collect_cycles 递增 gc_cycles）
    assert_eq!(heap.stats(), before);
}
