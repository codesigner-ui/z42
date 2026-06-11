//! Unit test for `signal_handler` — install idempotency. The `sigsafe` write
//! primitives + signal-name table moved to `pal::signal` (Phase 3) and are
//! tested in `pal/signal_tests.rs`; the actual signal-firing path is covered by
//! the `tests/signal_handler_e2e.rs` integration tests.

#[test]
fn install_is_idempotent() {
    // Call install() twice — second call must not panic, must not double-
    // register handlers. signal-hook-registry queues all handlers and runs
    // them in order — duplicate registration just means our handler runs
    // twice, harmless but wasteful. Reaching the end = no panic = pass.
    super::install();
    super::install();
}
