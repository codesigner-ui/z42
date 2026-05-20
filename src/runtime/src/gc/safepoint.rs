//! GC safepoint protocol (add-gc-safepoint, 2026-05-20).
//!
//! Cooperative polling safepoint for the interp dispatch loop. Mutators
//! call [`check_safepoint`] at strategic points (function entry, backward
//! branches, Call return). The GC driver calls [`request_gc_pause`] which
//! blocks until every other `VmContext` has parked, runs mark+sweep while
//! holding the returned [`GcPauseGuard`], then drops the guard to release
//! everyone.
//!
//! State machine:
//!
//! ```text
//! Idle ──(request_gc_pause)──▶ Requested ──(all parked)──▶ Marking
//!   ▲                                                          │
//!   └────────────(GcPauseGuard::drop)────────────────────────  │
//! ```
//!
//! Mutators sleep on `gc_phase_cv` until phase returns to `Idle`. The
//! collector also sleeps on the same Condvar while waiting for `parked_count`
//! to reach `vm_contexts.len() - 1` (collector itself is excluded). The
//! collector re-reads `vm_contexts.len()` on each wakeup so a new VmContext
//! registered mid-pause doesn't strand the collector.
//!
//! v0 scope: interp only. JIT-compiled code lacks the Rust-level instrumentation
//! point — covered by follow-up `add-gc-safepoint-jit` (see Decision 5 in
//! `docs/spec/archive/2026-05-20-add-gc-safepoint/design.md`).

use crate::vm_context::VmContext;
use std::sync::atomic::Ordering;

/// Current GC phase observed by mutators at safepoint checks.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GcPhase {
    /// No GC in progress; mutators run normally.
    Idle,
    /// Collector has requested a pause; mutators must park at the next safepoint.
    Requested,
    /// Collector is currently doing mark+sweep; mutators stay parked.
    Marking,
}

/// Fast-path safepoint check called from interp hot path.
///
/// Common case (no pending GC) takes one Mutex lock + one enum compare and
/// returns. When the GC has requested a pause, branches off to the slow
/// [`park_until_idle`] path.
#[inline]
pub fn check_safepoint(ctx: &VmContext) {
    let phase = *ctx.core.gc_phase.lock();
    if matches!(phase, GcPhase::Requested | GcPhase::Marking) {
        park_until_idle(ctx);
    }
}

/// Slow path — the mutator parks on the Condvar until the collector
/// transitions phase back to `Idle`.
fn park_until_idle(ctx: &VmContext) {
    ctx.core.parked_count.fetch_add(1, Ordering::AcqRel);
    // Notify the collector in case it's polling parked_count vs threshold.
    ctx.core.gc_phase_cv.notify_all();

    let mut phase = ctx.core.gc_phase.lock();
    while !matches!(*phase, GcPhase::Idle) {
        ctx.core.gc_phase_cv.wait(&mut phase);
    }
    drop(phase);

    ctx.core.parked_count.fetch_sub(1, Ordering::AcqRel);
}

/// RAII guard returned by [`request_gc_pause`]. While held, the collector
/// is in the `Marking` phase and all *other* VmContexts are parked. Drop
/// releases everyone.
pub struct GcPauseGuard<'a> {
    ctx: &'a VmContext,
}

/// Collector-side entry. Transitions `Idle → Requested`, waits for every
/// other live VmContext to park, then transitions `Requested → Marking`
/// and returns the guard. Caller does mark+sweep, then drops the guard to
/// transition `Marking → Idle` and notify all parked mutators.
///
/// The collector itself is **never** counted in `parked_count`; only other
/// VmContexts are waited for. If the collector is the only live VmContext
/// (`vm_contexts.len() == 1`), the wait condition `need_parked == 0` is
/// satisfied immediately.
pub fn request_gc_pause(ctx: &VmContext) -> GcPauseGuard<'_> {
    *ctx.core.gc_phase.lock() = GcPhase::Requested;

    // Wait for everyone-but-self to park. Re-read vm_contexts.len() on
    // each wakeup so a freshly-registered VmContext (which will see
    // Requested at its first safepoint check and park itself) doesn't
    // strand us with a stale threshold.
    let mut phase = ctx.core.gc_phase.lock();
    loop {
        let total = ctx.core.vm_contexts.lock().len();
        let need  = total.saturating_sub(1);
        if ctx.core.parked_count.load(Ordering::Acquire) >= need {
            break;
        }
        ctx.core.gc_phase_cv.wait(&mut phase);
    }
    *phase = GcPhase::Marking;
    drop(phase);

    GcPauseGuard { ctx }
}

impl Drop for GcPauseGuard<'_> {
    fn drop(&mut self) {
        *self.ctx.core.gc_phase.lock() = GcPhase::Idle;
        self.ctx.core.gc_phase_cv.notify_all();
    }
}

#[cfg(test)]
#[path = "safepoint_tests.rs"]
mod safepoint_tests;
