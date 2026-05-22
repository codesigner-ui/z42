//! add-generational-gc tests — P0 surface tested in
//! `gc::region::region_tests`; this module covers the integration
//! against `ArcMagrGC` + write-barrier override + (future) minor/major
//! GC dispatch.

use super::*;
use crate::gc::{GcMode, GcRef, MagrGC};
use crate::gc::region::PROMOTION_THRESHOLD;

// Helpers to make Rust GcRef and Value::Object/Array allocations
// look idiomatic in test fixtures.

fn alloc_obj(heap: &ArcMagrGC, name: &str) -> Value {
    heap.alloc_object(dummy_type_desc(name), vec![Value::Null], NativeData::None)
}

fn alloc_arr(heap: &ArcMagrGC, len: usize) -> Value {
    heap.alloc_array(vec![Value::Null; len])
}

fn gen_age_of(v: &Value) -> u8 {
    match v {
        Value::Object(gc) => GcRef::gen_age(gc),
        Value::Array(gc)  => GcRef::gen_age(gc),
        _ => unreachable!("test helper expects heap ref"),
    }
}

fn promote_to_old(v: &Value) {
    // Loop entry.gen_age.fetch_add until >= PROMOTION_THRESHOLD.
    // We can't call Region::promote without a handle; for tests we
    // simulate "this object survived N minor GCs" by directly bumping
    // gen_age via the entry. SAFETY: tests own the heap; entries
    // stay valid.
    let entry_ptr = match v {
        Value::Object(gc) => gc.entry_ptr().cast::<u8>(),
        Value::Array(gc)  => gc.entry_ptr().cast::<u8>(),
        _ => unreachable!(),
    };
    // Re-cast via the actual entry type. We use trick via the GcRef
    // helpers.
    match v {
        Value::Object(gc) => {
            for _ in 0..PROMOTION_THRESHOLD {
                let e = unsafe { gc.entry_ptr().as_ref() };
                e.gen_age.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            }
        }
        Value::Array(gc) => {
            for _ in 0..PROMOTION_THRESHOLD {
                let e = unsafe { gc.entry_ptr().as_ref() };
                e.gen_age.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            }
        }
        _ => unreachable!(),
    }
    let _ = entry_ptr;
}

// ── P1: GcMode variant ──────────────────────────────────────────────────────

#[test]
fn generational_mode_set_observable() {
    let heap = ArcMagrGC::new();
    assert_eq!(heap.mode(), GcMode::StwMarkSweep);
    heap.set_mode(GcMode::GenerationalMarkSweep);
    assert_eq!(heap.mode(), GcMode::GenerationalMarkSweep);
}

// ── P1: Barrier override + cross-gen card marking ──────────────────────────

#[test]
fn barrier_marks_card_on_old_to_young_field_write() {
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::GenerationalMarkSweep);

    // Alloc owner + child; promote owner so it counts as old.
    let owner = alloc_obj(&heap, "OwnerOld");
    let child_young = alloc_obj(&heap, "ChildYoung");
    promote_to_old(&owner);
    assert!(gen_age_of(&owner) >= PROMOTION_THRESHOLD);
    assert_eq!(gen_age_of(&child_young), 0);

    // Owner's chunk should be clean before the barrier dispatch.
    let owner_chunk = match &owner {
        Value::Object(gc) => {
            let e = unsafe { gc.entry_ptr().as_ref() };
            e.location.0
        }
        _ => unreachable!(),
    };
    // Need to peek at region without holding the lock through assertion.
    // Use a scoped block to drop the lock.
    let clean_before = {
        let r = heap.region_object_for_test().lock();
        !r.is_card_dirty(owner_chunk)
    };
    assert!(clean_before);

    heap.write_barrier_field(&owner, 0, &child_young);

    let dirty_after = {
        let r = heap.region_object_for_test().lock();
        r.is_card_dirty(owner_chunk)
    };
    assert!(dirty_after, "cross-gen old→young write marks owner's chunk dirty");
}

#[test]
fn barrier_no_card_on_young_to_young_write() {
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::GenerationalMarkSweep);

    let owner_young = alloc_obj(&heap, "OwnerYoung");
    let child_young = alloc_obj(&heap, "ChildYoung");

    let owner_chunk = match &owner_young {
        Value::Object(gc) => {
            let e = unsafe { gc.entry_ptr().as_ref() };
            e.location.0
        }
        _ => unreachable!(),
    };

    heap.write_barrier_field(&owner_young, 0, &child_young);

    let dirty = {
        let r = heap.region_object_for_test().lock();
        r.is_card_dirty(owner_chunk)
    };
    assert!(!dirty, "young→young writes do not mark cards");
}

#[test]
fn barrier_no_card_on_old_to_old_write() {
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::GenerationalMarkSweep);

    let owner_old = alloc_obj(&heap, "OwnerOld");
    let child_old = alloc_obj(&heap, "ChildOld");
    promote_to_old(&owner_old);
    promote_to_old(&child_old);

    let owner_chunk = match &owner_old {
        Value::Object(gc) => {
            let e = unsafe { gc.entry_ptr().as_ref() };
            e.location.0
        }
        _ => unreachable!(),
    };

    heap.write_barrier_field(&owner_old, 0, &child_old);

    let dirty = {
        let r = heap.region_object_for_test().lock();
        r.is_card_dirty(owner_chunk)
    };
    assert!(!dirty, "old→old writes do not mark cards (cross-gen does not apply)");
}

#[test]
fn barrier_no_op_in_stw_mode_even_under_cross_gen_setup() {
    // Even with manually-set gen_age values, the STW mode barrier
    // never marks cards. Regression guard.
    let heap = ArcMagrGC::new();
    assert_eq!(heap.mode(), GcMode::StwMarkSweep);

    let owner = alloc_obj(&heap, "Owner");
    let child = alloc_obj(&heap, "Child");
    promote_to_old(&owner);

    let owner_chunk = match &owner {
        Value::Object(gc) => {
            let e = unsafe { gc.entry_ptr().as_ref() };
            e.location.0
        }
        _ => unreachable!(),
    };

    heap.write_barrier_field(&owner, 0, &child);

    let dirty = {
        let r = heap.region_object_for_test().lock();
        r.is_card_dirty(owner_chunk)
    };
    assert!(!dirty, "STW mode never marks cards regardless of gen_age");
}

#[test]
fn barrier_array_path_marks_card_on_cross_gen() {
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::GenerationalMarkSweep);

    let arr_old = alloc_arr(&heap, 4);
    let child_young = alloc_obj(&heap, "Child");
    promote_to_old(&arr_old);

    let arr_chunk = match &arr_old {
        Value::Array(gc) => {
            let e = unsafe { gc.entry_ptr().as_ref() };
            e.location.0
        }
        _ => unreachable!(),
    };

    heap.write_barrier_array_elem(&arr_old, 0, &child_young);

    let dirty = {
        let r = heap.region_array_for_test().lock();
        r.is_card_dirty(arr_chunk)
    };
    assert!(dirty, "array path: cross-gen elem write marks arr's chunk");
}

// ── P1: parity check — existing STW behavior unchanged ──────────────────────

#[test]
fn cycle_collection_under_generational_mode_still_frees_garbage() {
    // P1 dispatches generational mode to the STW path (stub). Full
    // collect should still free unrooted cycles, identical to STW
    // mode. P2 replaces with minor/major; until then we verify
    // bookkeeping doesn't break basic correctness.
    let heap = ArcMagrGC::new();
    heap.set_mode(GcMode::GenerationalMarkSweep);

    let a = alloc_obj(&heap, "A");
    let b = alloc_obj(&heap, "B");
    {
        let Value::Object(a_gc) = &a else { panic!() };
        let Value::Object(b_gc) = &b else { panic!() };
        a_gc.borrow_mut().slots[0] = b.clone();
        b_gc.borrow_mut().slots[0] = a.clone();
    }
    drop(a);
    drop(b);

    heap.force_collect();

    let mut alive = 0;
    heap.iterate_live_objects(&mut |_| alive += 1);
    assert_eq!(alive, 0, "generational mode (P1 stub) still frees unrooted cycle via STW dispatch");
}
