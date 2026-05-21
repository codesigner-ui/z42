//! GC mode selection (add-concurrent-gc P0, 2026-05-22).
//!
//! [`GcMode`] selects which collection algorithm `ArcMagrGC` uses. The
//! default is `StwMarkSweep` — the proven path that landed in
//! `add-mark-sweep-collector`. `ConcurrentMarkSweep` is opt-in via
//! [`ArcMagrGC::set_mode`] or the `Z42_GC_MODE=concurrent` env var.
//!
//! The switch is the conservative shape: production keeps the safe
//! default; experimental concurrent code lives behind an explicit
//! opt-in. Future generational / semispace modes plug into the same
//! enum without further refactoring.

/// Selectable GC algorithm. Encoded as `u8` so `ArcMagrGC` can hold
/// the active mode in an `AtomicU8` field for lock-free mode reads on
/// the write-barrier hot path.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GcMode {
    /// Default: stop-the-world mark-sweep. Reachable objects survive;
    /// unreachable objects are freed. Mark + sweep both pause all
    /// mutators. Landed in `add-mark-sweep-collector` (2026-05-21).
    StwMarkSweep = 0,
    /// Concurrent mark + STW sweep. STW root snapshot → background
    /// mark BFS while mutators run → short STW handshake to drain
    /// final-burst → STW sweep. Tricolor incremental update; barrier
    /// shades new heap-ref writes gray. Landing across
    /// `add-concurrent-gc` P0–P7.
    ConcurrentMarkSweep = 1,
}

impl Default for GcMode {
    fn default() -> Self { GcMode::StwMarkSweep }
}

impl GcMode {
    /// Resolve the GC mode from the `Z42_GC_MODE` environment variable.
    /// Unset → `StwMarkSweep` (default). Invalid value → fall back to
    /// `StwMarkSweep` with a stderr warning so misconfigured production
    /// runs don't silently land on the wrong algorithm.
    pub fn from_env() -> Self {
        match std::env::var("Z42_GC_MODE") {
            Ok(s) => match s.as_str() {
                "concurrent" | "concurrent-mark-sweep" => GcMode::ConcurrentMarkSweep,
                "stw" | "stw-mark-sweep" => GcMode::StwMarkSweep,
                "" => GcMode::StwMarkSweep,
                other => {
                    eprintln!(
                        "z42: Z42_GC_MODE={:?} not recognized; falling back to stw-mark-sweep",
                        other
                    );
                    GcMode::StwMarkSweep
                }
            },
            Err(_) => GcMode::StwMarkSweep,
        }
    }

    /// Convert from the `u8` representation used by `AtomicU8` storage.
    /// Returns `StwMarkSweep` for unknown values — defensive default
    /// so that even corrupt storage can't crash on a `match` exhaustivity
    /// check.
    pub fn from_u8(v: u8) -> Self {
        match v {
            0 => GcMode::StwMarkSweep,
            1 => GcMode::ConcurrentMarkSweep,
            _ => GcMode::StwMarkSweep,
        }
    }
}

#[cfg(test)]
mod mode_tests {
    use super::*;

    #[test]
    fn default_is_stw_mark_sweep() {
        assert_eq!(GcMode::default(), GcMode::StwMarkSweep);
    }

    #[test]
    fn from_u8_roundtrips_known_variants() {
        assert_eq!(GcMode::from_u8(GcMode::StwMarkSweep as u8), GcMode::StwMarkSweep);
        assert_eq!(GcMode::from_u8(GcMode::ConcurrentMarkSweep as u8), GcMode::ConcurrentMarkSweep);
    }

    #[test]
    fn from_u8_unknown_falls_back_to_stw() {
        assert_eq!(GcMode::from_u8(99), GcMode::StwMarkSweep);
        assert_eq!(GcMode::from_u8(255), GcMode::StwMarkSweep);
    }

    // Note: from_env() tests cannot reliably set env vars in a unit test
    // (Rust test harness shares process state across parallel tests). The
    // env-var path is exercised by the integration test in P0.9 (running
    // `Z42_GC_MODE=concurrent ./scripts/test-all.sh`) and verified via
    // ArcMagrGC::new() construction in `arc_heap_tests::mode_selection`.
}
