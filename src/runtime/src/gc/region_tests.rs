//! Unit tests for `gc/region.rs` —— add-custom-allocator P0.
//!
//! Coverage:
//! - Allocation: empty → first chunk → grow across chunks
//! - Pointer stability: `&RegionEntry` address is stable across
//!   subsequent allocs (CRITICAL for `GcRef::as_ptr` identity hashing)
//! - Free list reuse: tombstoned slot pops back into use
//! - Generation bumping: tombstone bumps gen; stale handle no longer
//!   matches
//! - Iteration: walks live entries in order, skips tombstoned

use super::*;
use std::sync::atomic::Ordering;

#[test]
fn alloc_into_empty_region_creates_first_chunk_and_entry() {
    let mut r: Region<u64> = Region::new();
    assert_eq!(r.alive_count(), 0);

    let h = r.alloc(42);
    assert_eq!(h.chunk_idx, 0);
    assert_eq!(h.entry_idx, 0);
    assert_eq!(h.generation, 0);

    assert_eq!(r.alive_count(), 1);
    let e = r.resolve(h);
    assert_eq!(*e.value.lock(), 42);
    assert!(e.alive.load(Ordering::Acquire));
}

#[test]
fn alloc_grows_chunk_when_full() {
    let mut r: Region<u64> = Region::new();
    let mut handles = Vec::with_capacity(CHUNK_SIZE + 1);

    for i in 0..(CHUNK_SIZE + 5) {
        handles.push(r.alloc(i as u64));
    }

    // First CHUNK_SIZE handles in chunk 0; next 5 in chunk 1.
    assert_eq!(handles[0].chunk_idx, 0);
    assert_eq!(handles[CHUNK_SIZE - 1].chunk_idx, 0);
    assert_eq!(handles[CHUNK_SIZE].chunk_idx, 1);
    assert_eq!(handles[CHUNK_SIZE].entry_idx, 0);
    assert_eq!(handles[CHUNK_SIZE + 4].chunk_idx, 1);
    assert_eq!(handles[CHUNK_SIZE + 4].entry_idx, 4);

    assert_eq!(r.alive_count(), CHUNK_SIZE + 5);
}

#[test]
fn alloc_pointer_stability_across_growth() {
    // CRITICAL: `GcRef::as_ptr` returns &Mutex<T> inside RegionEntry.
    // That address MUST remain stable as the region grows past chunk
    // boundaries. Verified by recording an address pre-grow and
    // checking equality post-grow.
    let mut r: Region<u64> = Region::new();

    let h0 = r.alloc(100);
    let addr_before: *const RegionEntry<u64> = r.resolve(h0);

    // Force multiple chunk growths.
    for i in 0..(3 * CHUNK_SIZE + 10) {
        r.alloc(i as u64);
    }

    let addr_after: *const RegionEntry<u64> = r.resolve(h0);
    assert_eq!(addr_before, addr_after,
        "RegionEntry address must not move when region grows");

    // Inner value also intact.
    let e = r.resolve(h0);
    assert_eq!(*e.value.lock(), 100);
}

#[test]
fn tombstone_marks_dead_and_pushes_free_list() {
    let mut r: Region<u64> = Region::new();
    let h = r.alloc(7);

    assert_eq!(r.alive_count(), 1);
    assert!(r.resolve(h).alive.load(Ordering::Acquire));

    let ok = r.tombstone(h);
    assert!(ok, "first tombstone succeeds");
    assert_eq!(r.alive_count(), 0);
    assert!(!r.resolve(h).alive.load(Ordering::Acquire));
}

#[test]
fn free_list_reuses_tombstoned_slot() {
    let mut r: Region<u64> = Region::new();
    let h1 = r.alloc(10);
    let h2 = r.alloc(20);
    assert_eq!(r.alive_count(), 2);

    r.tombstone(h1);
    assert_eq!(r.alive_count(), 1);

    let h3 = r.alloc(30);
    // h3 should reuse h1's slot.
    assert_eq!(h3.chunk_idx, h1.chunk_idx);
    assert_eq!(h3.entry_idx, h1.entry_idx);
    // But generation bumped, so h1 (stale) != h3.
    assert_ne!(h3.generation, h1.generation);
    // h2 untouched.
    assert_eq!(*r.resolve(h2).value.lock(), 20);
    assert_eq!(*r.resolve(h3).value.lock(), 30);
}

#[test]
fn tombstone_bumps_generation() {
    let mut r: Region<u64> = Region::new();
    let h = r.alloc(1);
    let gen_before = r.resolve(h).generation.load(Ordering::Acquire);
    r.tombstone(h);
    let gen_after = r.resolve(h).generation.load(Ordering::Acquire);
    assert_eq!(gen_after, gen_before + 1,
        "tombstone bumps generation by exactly 1");
}

#[test]
fn tombstone_stale_handle_is_noop() {
    let mut r: Region<u64> = Region::new();
    let h1 = r.alloc(1);
    r.tombstone(h1);

    // Allocate fresh into the slot.
    let h2 = r.alloc(2);
    assert_eq!(h2.chunk_idx, h1.chunk_idx);
    assert_eq!(h2.entry_idx, h1.entry_idx);

    // Stale h1 now has wrong generation; tombstone should be no-op.
    let ok = r.tombstone(h1);
    assert!(!ok, "stale tombstone is no-op (generation mismatch)");
    // h2 still alive.
    assert!(r.resolve(h2).alive.load(Ordering::Acquire));
    assert_eq!(*r.resolve(h2).value.lock(), 2);
}

#[test]
fn iterate_alive_visits_in_alloc_order_skipping_tombstoned() {
    let mut r: Region<u64> = Region::new();
    let _h0 = r.alloc(100);
    let h1 = r.alloc(200);
    let _h2 = r.alloc(300);
    r.tombstone(h1);

    let mut seen = Vec::new();
    r.iterate_alive(|_h, e| {
        seen.push(*e.value.lock());
    });
    assert_eq!(seen, vec![100, 300],
        "iterate skips tombstoned entry (h1=200)");
}

#[test]
fn iterate_alive_handles_grow_correctly() {
    let mut r: Region<u64> = Region::new();
    for i in 0..(CHUNK_SIZE + 3) {
        r.alloc(i as u64);
    }
    let mut count = 0;
    r.iterate_alive(|_h, _e| count += 1);
    assert_eq!(count, CHUNK_SIZE + 3);
}

#[test]
fn region_entry_mark_cas_idempotent() {
    let mut r: Region<u64> = Region::new();
    let h = r.alloc(0);
    let e = r.resolve(h);

    assert!(!e.is_marked());
    assert!(e.mark(), "first mark CAS succeeds");
    assert!(e.is_marked());
    assert!(!e.mark(), "second mark CAS fails (already marked)");

    e.clear_mark();
    assert!(!e.is_marked());
    assert!(e.mark(), "after clear, mark succeeds again");
}

#[test]
fn total_capacity_and_free_slot_count_track_growth() {
    let mut r: Region<u64> = Region::new();
    assert_eq!(r.total_capacity(), 0);
    assert_eq!(r.free_slot_count(), 0);

    r.alloc(1);
    assert_eq!(r.total_capacity(), CHUNK_SIZE);
    assert_eq!(r.free_slot_count(), CHUNK_SIZE - 1);

    let h = r.alloc(2);
    r.tombstone(h);
    // CHUNK_SIZE - 2 (bump remaining) + 1 (tombstone) = CHUNK_SIZE - 1
    assert_eq!(r.free_slot_count(), CHUNK_SIZE - 1);
}

#[test]
fn region_drop_runs_each_initialized_entry_drop() {
    use std::sync::atomic::AtomicUsize;
    use std::sync::Arc as StdArc;

    let drop_count = StdArc::new(AtomicUsize::new(0));

    struct DropCounter(StdArc<AtomicUsize>);
    impl Drop for DropCounter {
        fn drop(&mut self) {
            self.0.fetch_add(1, Ordering::AcqRel);
        }
    }

    {
        let mut r: Region<DropCounter> = Region::new();
        r.alloc(DropCounter(drop_count.clone()));
        r.alloc(DropCounter(drop_count.clone()));
        r.alloc(DropCounter(drop_count.clone()));
        // Region drops here.
    }

    // Expect 3 user-data drops (one per alloc'd entry).
    assert_eq!(drop_count.load(Ordering::Acquire), 3);
}

#[test]
fn region_drop_does_not_touch_tombstoned_slots_double_drop() {
    use std::sync::atomic::AtomicUsize;
    use std::sync::Arc as StdArc;

    let drop_count = StdArc::new(AtomicUsize::new(0));

    struct DropCounter(StdArc<AtomicUsize>);
    impl Drop for DropCounter {
        fn drop(&mut self) {
            self.0.fetch_add(1, Ordering::AcqRel);
        }
    }

    {
        let mut r: Region<DropCounter> = Region::new();
        let h1 = r.alloc(DropCounter(drop_count.clone()));
        let _h2 = r.alloc(DropCounter(drop_count.clone()));
        r.tombstone(h1);
        // Tombstoning does NOT drop the user value (the slot still
        // owns a live RegionEntry holding the user value behind a
        // Mutex). At alloc reuse, the old entry is replaced in place
        // — old entry's Drop runs then, which would drop the user
        // value. But if we don't reuse, Region drop must drop it.
    }

    // Both user values dropped exactly once at region drop.
    // (Tombstone alone doesn't drop; that's by design — finalizer
    // dispatch is the caller's job.)
    assert_eq!(drop_count.load(Ordering::Acquire), 2);
}

#[test]
fn region_drop_after_slot_reuse_drops_each_value_exactly_once() {
    use std::sync::atomic::AtomicUsize;
    use std::sync::Arc as StdArc;

    let drop_count = StdArc::new(AtomicUsize::new(0));

    struct DropCounter(StdArc<AtomicUsize>);
    impl Drop for DropCounter {
        fn drop(&mut self) {
            self.0.fetch_add(1, Ordering::AcqRel);
        }
    }

    {
        let mut r: Region<DropCounter> = Region::new();
        let h1 = r.alloc(DropCounter(drop_count.clone()));
        r.tombstone(h1);
        // The tombstoned slot is reused on next alloc; the in-place
        // replacement drops the old user value.
        r.alloc(DropCounter(drop_count.clone()));
        // After reuse: drop_count = 1 (original v1 was dropped when
        // alloc replaced the entry).
        assert_eq!(drop_count.load(Ordering::Acquire), 1);
        // Region drop here releases the second value.
    }
    assert_eq!(drop_count.load(Ordering::Acquire), 2);
}
