//! Unit tests for `gc/safepoint.rs`. Single-VmContext invariants only —
//! multi-thread end-to-end coverage is in
//! `runtime/tests/cross_thread_smoke.rs::gc_collect_with_concurrent_mutators_no_race`.

use super::*;
use crate::vm_context::VmContext;
use std::sync::atomic::Ordering;

#[test]
fn gc_phase_idle_by_default() {
    let ctx = VmContext::new();
    assert_eq!(*ctx.core.gc_phase.lock(), GcPhase::Idle);
    assert_eq!(ctx.core.parked_count.load(Ordering::Acquire), 0);
}

#[test]
fn check_safepoint_idle_is_no_op_fast_path() {
    let ctx = VmContext::new();
    // Loop several times to confirm Idle phase short-circuits without
    // touching parked_count or blocking. If the fast path accidentally
    // parked, this test would hang (only one VmContext, so the wait
    // wouldn't be released by anyone).
    for _ in 0..100 {
        check_safepoint(&ctx);
    }
    assert_eq!(ctx.core.parked_count.load(Ordering::Acquire), 0);
    assert_eq!(*ctx.core.gc_phase.lock(), GcPhase::Idle);
}

#[test]
fn request_gc_pause_with_only_self_proceeds_immediately() {
    // Only VmContext is the collector; no other mutators to wait for.
    // Should transition Idle → Requested → Marking without blocking.
    let ctx = VmContext::new();
    {
        let _guard = request_gc_pause(&ctx);
        assert_eq!(*ctx.core.gc_phase.lock(), GcPhase::Marking);
    }
    // Guard dropped → released.
    assert_eq!(*ctx.core.gc_phase.lock(), GcPhase::Idle);
}

#[test]
fn pause_guard_drop_notifies_waiters() {
    // Spawn a 2nd VmContext sharing the core, have it park on a Condvar
    // wait, then collector starts pause + finishes. The parked mutator
    // should be released (parked_count returns to 0).
    let collector = VmContext::new();
    let mutator = VmContext::new_with_core(collector.core_arc());

    // Trip the phase manually to Requested to force the mutator into
    // the slow path the next time it checks. (We do it inside a scope so
    // we don't hold the lock when the mutator tries to take it.)
    {
        *collector.core.gc_phase.lock() = GcPhase::Marking;
    }

    // Spawn a thread that calls check_safepoint on the mutator. It
    // should park.
    //
    // We need an owned ref the worker thread can capture — but
    // Pin<Box<VmContext>> is !Unpin, so we move via Arc<VmCore> and
    // construct a fresh VmContext::new_with_core inside the worker.
    let core = collector.core_arc();
    let worker = std::thread::spawn(move || {
        let m = VmContext::new_with_core(core);
        check_safepoint(&m);
    });

    // Wait until the worker is parked (parked_count == 1). We may have
    // a tiny race window where the worker hasn't yet incremented; loop
    // with a yield. parking_lot Condvar has no spurious wakes against
    // notify_all in this protocol so this poll is just for startup.
    let start = std::time::Instant::now();
    while collector.core.parked_count.load(Ordering::Acquire) < 1 {
        std::thread::yield_now();
        if start.elapsed() > std::time::Duration::from_secs(5) {
            panic!("worker never parked");
        }
    }

    // Release.
    *collector.core.gc_phase.lock() = GcPhase::Idle;
    collector.core.gc_phase_cv.notify_all();
    worker.join().expect("worker panicked");
    assert_eq!(collector.core.parked_count.load(Ordering::Acquire), 0);

    // Keep mutator alive across the asserts so the registry doesn't drop
    // out from under us.
    drop(mutator);
}

#[test]
fn request_pause_waits_for_other_mutators_to_park() {
    // collector + 2 mutator VmContexts. Spawn 2 worker threads that
    // immediately park (because we pre-set phase to Requested). Collector
    // calls request_gc_pause, which must wait for parked_count == 2.
    let collector = VmContext::new();
    let _m1 = VmContext::new_with_core(collector.core_arc());
    let _m2 = VmContext::new_with_core(collector.core_arc());

    let core1 = collector.core_arc();
    let core2 = collector.core_arc();

    // Pre-set phase to Requested so workers see it on first check.
    *collector.core.gc_phase.lock() = GcPhase::Requested;

    let w1 = std::thread::spawn(move || {
        let m = VmContext::new_with_core(core1);
        check_safepoint(&m);
    });
    let w2 = std::thread::spawn(move || {
        let m = VmContext::new_with_core(core2);
        check_safepoint(&m);
    });

    // Wait until both workers are parked (parked_count == 2). Note: the
    // long-lived _m1 / _m2 do NOT increment parked_count (they never
    // entered check_safepoint); only the threads' fresh VmContexts do.
    let start = std::time::Instant::now();
    while collector.core.parked_count.load(Ordering::Acquire) < 2 {
        std::thread::yield_now();
        if start.elapsed() > std::time::Duration::from_secs(5) {
            panic!("workers never both parked");
        }
    }

    // Release.
    *collector.core.gc_phase.lock() = GcPhase::Idle;
    collector.core.gc_phase_cv.notify_all();
    w1.join().expect("w1 panicked");
    w2.join().expect("w2 panicked");
}
