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
///
/// **add-concurrent-gc P1 (2026-05-22)**: extended with `ConcurrentMarking`.
///
/// State machines:
///
/// ```text
/// STW path (GcMode::StwMarkSweep, default):
///   Idle ─►Requested─►Marking─►Idle
///                       ▲
///                       │ (mutators parked throughout Marking)
///
/// Concurrent path (GcMode::ConcurrentMarkSweep, opt-in):
///   Idle ─►Requested─►ConcurrentMarking─►Marking─►Idle
///                            ▲              ▲
///                            │              │ (short STW handshake
///                            │              │  for queue drain + sweep)
///                            │
///                       (mutators RUN; write barriers
///                        push gray refs to mark queue)
/// ```
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GcPhase {
    /// No GC in progress; mutators run normally.
    Idle,
    /// Collector has requested a pause; mutators must park at the next safepoint.
    Requested,
    /// STW phase — collector is doing mark+sweep (default path) or the
    /// termination handshake + sweep (concurrent path); mutators parked.
    Marking,
    /// **add-concurrent-gc P1 (2026-05-22)**: concurrent mark phase —
    /// only set under `GcMode::ConcurrentMarkSweep`. Mutators continue
    /// executing during this phase (the write-barrier override is
    /// responsible for shading gray new refs). Transitions to `Marking`
    /// when the collector requests the final STW handshake.
    ///
    /// `check_safepoint_slow` explicitly does NOT park mutators when
    /// this phase is observed — that's the entire point of the
    /// concurrent path. Other phases (Requested / Marking) keep their
    /// STW parking semantics.
    ConcurrentMarking,
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
    // add-concurrent-gc P1: `ConcurrentMarking` is observable but mutators
    // do NOT park — concurrent mark requires mutators to keep running so
    // the background mark thread isn't the only one making progress. The
    // write-barrier override (P3) handles tricolor shading on writes.
    if matches!(phase, GcPhase::Requested | GcPhase::Marking) {
        park_until_idle(ctx);
        return;
    }
    // Idle phase — drain pending auto-collect if any.
    if ctx.core.needs_auto_collect.swap(false, Ordering::AcqRel) {
        // add-multi-collector-arbitration (2026-05-21): request_gc_pause
        // returns Option. None means another collector is already active
        // — we've been park-as-mutator'd inside the call; just return.
        if let Some(_pause) = request_gc_pause(ctx) {
            ctx.heap().collect_cycles();
            // _pause Drop releases the world + notifies all parked mutators.
        }
    }
}

/// Slow path — the mutator parks on the Condvar until the collector
/// releases the world. Releases on `Idle` *or* `ConcurrentMarking`
/// (add-concurrent-gc P1): the concurrent path transitions
/// `Requested → ConcurrentMarking` to signal mutators may resume; only
/// the final STW handshake (`Marking`) re-parks them.
fn park_until_idle(ctx: &VmContext) {
    ctx.core.parked_count.fetch_add(1, Ordering::AcqRel);
    // Notify the collector in case it's polling parked_count vs threshold.
    ctx.core.gc_phase_cv.notify_all();

    let mut phase = ctx.core.gc_phase.lock();
    while matches!(*phase, GcPhase::Requested | GcPhase::Marking) {
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
/// **add-multi-collector-arbitration (2026-05-21)**: returns
/// `Option<GcPauseGuard>`. The leading CAS on `collector_active` ensures
/// only one thread can be the active collector at a time:
///
/// - `Some(guard)` — we claimed the collector role; caller proceeds with
///   `collect_cycles()` / `force_collect()`
/// - `None` — another collector is active. We've already parked-as-mutator
///   inside this call (contributing to the active collector's
///   `parked_count` target). Caller skips its collect.
///
/// The collector itself is **never** counted in `parked_count`; only other
/// VmContexts are waited for. If the collector is the only live VmContext
/// (`vm_contexts.len() == 1`), the wait condition `need_parked == 0` is
/// satisfied immediately.
pub fn request_gc_pause(ctx: &VmContext) -> Option<GcPauseGuard<'_>> {
    // Atomic CAS: claim the unique collector role. Acquire side pairs
    // with the previous collector's `Release` store in GcPauseGuard::drop
    // (so we see its heap changes); Release side pairs with our
    // subsequent `gc_phase = Requested` store (so workers seeing
    // Requested also see our collector_active = true).
    if ctx.core.collector_active
        .compare_exchange(false, true, Ordering::AcqRel, Ordering::Relaxed)
        .is_err()
    {
        // Another collector is active. Park-as-mutator so the active
        // collector's `parked_count` target is reached faster; return
        // None so caller skips its own collect.
        park_until_idle(ctx);
        return None;
    }

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

    Some(GcPauseGuard { ctx })
}

impl Drop for GcPauseGuard<'_> {
    fn drop(&mut self) {
        *self.ctx.core.gc_phase.lock() = GcPhase::Idle;
        self.ctx.core.gc_phase_cv.notify_all();
        // add-multi-collector-arbitration (2026-05-21): release the
        // exclusive collector claim. Release ordering so the next
        // collector's compare_exchange Acquire sees our final heap state.
        self.ctx.core.collector_active.store(false, Ordering::Release);
    }
}

#[cfg(test)]
#[path = "safepoint_tests.rs"]
mod safepoint_tests;
