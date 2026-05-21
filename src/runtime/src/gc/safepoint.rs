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
use std::sync::OnceLock;

/// add-gc-safepoint-counter-throttling (2026-05-21): default throttle
/// constant. Every Nth `check_safepoint` call runs the slow path (real
/// `gc_phase` Mutex lock + auto_collect drain); other N-1 calls are a
/// single atomic decrement.
///
/// 1024 mirrors HotSpot's polling-page heuristic — at z42's typical
/// per-iter cost (~50ns) it caps GC pause latency at ≈ 50us, which is
/// negligible compared to actual collect time (10ms+).
const DEFAULT_THROTTLE: u32 = 1024;

/// Cached throttle value. Resolved once from `Z42_SAFEPOINT_THROTTLE` env
/// on first access; subsequent calls hit the OnceLock fast path.
static THROTTLE: OnceLock<u32> = OnceLock::new();

/// Effective safepoint throttle. Reads `Z42_SAFEPOINT_THROTTLE` env on
/// first call; cached for the process lifetime. Invalid values fall back
/// to [`DEFAULT_THROTTLE`] with a warning on stderr.
///
/// Setting `Z42_SAFEPOINT_THROTTLE=1` disables throttling (every call
/// runs the slow path) — useful for debugging latency-sensitive paths.
pub fn throttle_n() -> u32 {
    *THROTTLE.get_or_init(|| match std::env::var("Z42_SAFEPOINT_THROTTLE") {
        Ok(s) => match s.parse::<u32>() {
            Ok(n) if n >= 1 => n,
            _ => {
                eprintln!(
                    "z42: invalid Z42_SAFEPOINT_THROTTLE={s:?}; using default {DEFAULT_THROTTLE}"
                );
                DEFAULT_THROTTLE
            }
        },
        Err(_) => DEFAULT_THROTTLE,
    })
}

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
/// **add-gc-safepoint-counter-throttling (2026-05-21)**: this fast path
/// is one `AtomicU32::fetch_sub(1, Relaxed) + compare + branch` (~3-5ns).
/// The Mutex-lock + phase-check + auto-collect-drain logic only runs every
/// [`throttle_n()`] th call (default 1024). Worker liveness under a GC
/// request is bounded by N iterations × per-iter cost — at typical z42
/// hot-loop iter (~50ns) this caps GC pause latency at ~50us, far below
/// actual collect time.
#[inline]
pub fn check_safepoint(ctx: &VmContext) {
    // Fast path: relaxed decrement; if counter was > 1 before, we still
    // have work to do before probing the real state.
    let prev = ctx.safepoint_skip.fetch_sub(1, Ordering::Relaxed);
    if prev > 1 {
        return;
    }
    // Slow path: counter just hit 0 (or wrapped to u32::MAX in a
    // theoretical overflow — saturating reset below restores invariant).
    ctx.safepoint_skip.store(throttle_n(), Ordering::Relaxed);
    check_safepoint_slow(ctx);
}

/// Slow-path safepoint check — Mutex lock + phase check + auto-collect
/// drain. Called from [`check_safepoint`] every Nth call (per
/// [`throttle_n`]).
///
/// **add-gc-safepoint-auto-threshold (2026-05-20)**: when phase is Idle
/// but the heap's pressure-trip path has set `needs_auto_collect = true`,
/// the calling thread atomically claims the collect round via `swap(false,
/// AcqRel)` and runs a stop-the-world collect under [`request_gc_pause`].
/// If multiple threads see the flag, only the first swap-true claims;
/// the rest see false and skip (subsequent allocs that still trip pressure
/// re-set the flag).
#[inline(never)]
fn check_safepoint_slow(ctx: &VmContext) {
    let phase = *ctx.core.gc_phase.lock();
    if matches!(phase, GcPhase::Requested | GcPhase::Marking) {
        park_until_idle(ctx);
        return;
    }
    // Idle phase — drain pending auto-collect if any.
    if ctx.core.needs_auto_collect.swap(false, Ordering::AcqRel) {
        let _pause = request_gc_pause(ctx);
        ctx.heap().collect_cycles();
        // _pause Drop releases the world + notifies all parked mutators.
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
