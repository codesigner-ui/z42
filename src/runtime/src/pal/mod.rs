//! Platform Abstraction Layer (PAL) — single home for every `#[cfg(...)]`
//! split in the runtime.
//!
//! review.md Part 1 P2 Phase 1 (2026-06-03, add-pal-system-phase1). See
//! `docs/design/runtime/pal.md` for the long-form design (CoreCLR
//! comparison, Phase 2-N migration plan, invariants every submodule must
//! respect).
//!
//! # Current submodules
//!
//! - [`system`] — hostname / OS version (Phase 1; this commit).
//!
//! # Future submodules (independent specs)
//!
//! - `fs` — file-system OS calls (Phase 2; migrates `corelib/fs.rs`
//!   make_executable / symlink).
//! - `signal` — POSIX signal handler dispatch (Phase 3; migrates
//!   `signal_handler.rs`).
//! - `thread` — pthread / Windows-thread primitives backing the future
//!   multi-thread runtime.
//! - `mem` — page-aligned alloc / mmap / mprotect for the GC bump
//!   allocator.

pub mod system;
