//! `RuntimeConfig` — centralized declaration of every `Z42_*` runtime knob
//! consumed by `main.rs` startup.
//!
//! Roslyn / CoreCLR parallel: `inc/clrconfigvalues.h` macro table that
//! registers every runtime knob with a default + type + description in
//! one place. docs/review.md Part 4 D1 — was P0 because adding a new
//! knob previously meant scattering `std::env::var("Z42_X")` calls
//! across `main.rs` / `signal_handler.rs` / `gc/` / `native/`, with no
//! single place to discover what knobs exist.
//!
//! # Scope (this Phase 1 refactor)
//!
//! Centralizes the **5 startup-consumed** env vars:
//! `Z42_LIBS` / `Z42_PATH` / `Z42_LOG` / `Z42_CRASH_DIR` / `Z42_TARGET`
//! (reserved). These are the knobs `--info` reports and that `main.rs`
//! reads at boot.
//!
//! Subsystem-local OnceLock-cached env reads (`Z42_NATIVE_PATH` in
//! `native/ext.rs`, `Z42_SAFEPOINT_THROTTLE` / `Z42_GC_*` in `gc/`,
//! `Z42_STRESS_ITERS` in tests) keep their existing inline reads —
//! migrating those is Phase 2 (separate small refactor). The `KNOWN_KNOBS`
//! table here lists ALL of them for discovery / `--info` purposes
//! even though the actual values are read elsewhere.
//!
//! # Why a `&'static [KnobSpec]` table not just struct fields
//!
//! Each knob has metadata (`name` / `description` / `default_hint`) that
//! `--info` needs to render. Putting them in a const table makes
//! enumeration trivial. The struct also stores the *resolved values*
//! for the 5 startup knobs; subsystem-local knobs are listed but not
//! pre-resolved (they need to stay lazy for OnceLock cache semantics).

use std::path::PathBuf;

/// Metadata for a single `Z42_*` knob. Used by `--info` + future docgen
/// to enumerate every runtime knob in one place. Keep `KNOWN_KNOBS`
/// alphabetically sorted by `name` for stable `--info` output.
#[derive(Debug, Clone, Copy)]
pub struct KnobSpec {
    /// Env var name (e.g. `"Z42_LIBS"`).
    pub name: &'static str,
    /// One-line human description shown by `--info` / docgen.
    pub description: &'static str,
    /// Hint string for the default when unset (e.g. `"unset; falls back to ..."`).
    pub default_hint: &'static str,
    /// Where this knob is actually consumed — file path under `src/runtime/src/`.
    pub consumed_by: &'static str,
}

/// Authoritative list of every `Z42_*` env var the runtime reads.
///
/// Adding a new knob: append to this table + implement reader at
/// `consumed_by` location. CI check ([future]: stdlib API surface lint)
/// can grep `Z42_` across `src/runtime/src/` and diff against this table
/// to catch stragglers.
pub const KNOWN_KNOBS: &[KnobSpec] = &[
    KnobSpec {
        name: "Z42_CRASH_DIR",
        description: "directory for panic + signal crash report files",
        default_hint: "unset; reports go to stderr only",
        consumed_by: "main.rs (panic hook) + signal_handler.rs",
    },
    KnobSpec {
        name: "Z42_GC_MINOR_THRESHOLD",
        description: "bytes of allocation before auto-trigger minor GC",
        default_hint: "unset; defaults to 64 KiB",
        consumed_by: "gc/arc_heap.rs",
    },
    KnobSpec {
        name: "Z42_GC_MODE",
        description: "GC algorithm: `stw` / `concurrent` / `generational` (with `-mark-sweep` aliases)",
        default_hint: "unset; defaults to `stw-mark-sweep`",
        consumed_by: "gc/mode.rs",
    },
    KnobSpec {
        name: "Z42_GC_PAUSE_WINDOW",
        description: "rolling window (ms) for GC pause statistics",
        default_hint: "unset; defaults to 60_000 ms",
        consumed_by: "gc/types.rs",
    },
    KnobSpec {
        name: "Z42_GC_SOFT_THRESHOLD",
        description: "heap pressure ratio (0.0–1.0) above which SoftHandle refs become GC-eligible",
        default_hint: "unset; defaults to 0.80",
        consumed_by: "gc/soft_registry.rs",
    },
    KnobSpec {
        name: "Z42_LIBS",
        description: "stdlib zpkg search directory",
        default_hint: "unset; falls back to artifacts/build/libs/release relative to z42vm binary",
        consumed_by: "main.rs",
    },
    KnobSpec {
        name: "Z42_LOG",
        description: "tracing-subscriber EnvFilter directive (e.g. z42::jit=debug,z42=warn)",
        default_hint: "unset; defaults to z42=warn (or z42=info under --verbose)",
        consumed_by: "main.rs (init_tracing)",
    },
    KnobSpec {
        name: "Z42_NATIVE_PATH",
        description: "search path for native .dylib/.so/.dll modules (colon-separated)",
        default_hint: "unset; falls back to package-relative search",
        consumed_by: "native/ext.rs",
    },
    KnobSpec {
        name: "Z42_PATH",
        description: "module search paths (colon-separated)",
        default_hint: "unset; falls back to <cwd>, <cwd>/modules",
        consumed_by: "main.rs",
    },
    KnobSpec {
        name: "Z42_SAFEPOINT_THROTTLE",
        description: "per-thread safepoint check throttle (skip N safepoints between heap polls)",
        default_hint: "unset; defaults to 1024",
        consumed_by: "gc/safepoint.rs",
    },
    KnobSpec {
        name: "Z42_STRESS_ITERS",
        description: "iteration count for GC stress tests (test code only)",
        default_hint: "unset; defaults to 100",
        consumed_by: "gc/arc_heap_tests/stress.rs",
    },
];

/// Resolved values of the **5 startup-consumed knobs**. Built once by
/// [`RuntimeConfig::from_env`] at the start of `main()` and threaded
/// through subsequent setup (tracing init / libs dir resolve / signal
/// handler crash dir / module path).
///
/// Subsystem-local knobs (`Z42_GC_*`, `Z42_NATIVE_PATH`, etc.) are NOT
/// stored here — they're read lazily via OnceLock at their consumer.
#[derive(Debug, Clone, Default)]
pub struct RuntimeConfig {
    /// `Z42_LIBS` — stdlib zpkg search dir override. `None` = use fallback.
    pub libs_dir: Option<PathBuf>,
    /// `Z42_PATH` — colon-separated module search paths.
    pub module_path: Vec<PathBuf>,
    /// `Z42_LOG` — tracing-subscriber filter directive. `None` = use default.
    pub log_filter: Option<String>,
    /// `Z42_CRASH_DIR` — panic / signal crash report directory. `None` = stderr only.
    pub crash_dir: Option<PathBuf>,
}

impl RuntimeConfig {
    /// Build from the process environment (POSIX getenv / Windows GetEnvironmentVariable).
    /// Empty strings are treated as unset.
    pub fn from_env() -> Self {
        Self::from_getter(|name| std::env::var(name).ok())
    }

    /// Build using an injectable env getter. Test-friendly form —
    /// avoids `std::env::set_var` global races when running cargo test in
    /// parallel. The getter returns `Some(string)` if "set" (any value),
    /// `None` if "unset".
    pub fn from_getter<F>(get: F) -> Self
    where
        F: Fn(&str) -> Option<String>,
    {
        Self {
            libs_dir:    get("Z42_LIBS")    .filter(|s| !s.trim().is_empty()).map(PathBuf::from),
            module_path: get("Z42_PATH")    .filter(|s| !s.trim().is_empty()).map(|s| split_paths(&s)).unwrap_or_default(),
            log_filter:  get("Z42_LOG")     .filter(|s| !s.trim().is_empty()),
            crash_dir:   get("Z42_CRASH_DIR").filter(|s| !s.trim().is_empty()).map(PathBuf::from),
        }
    }
}

fn split_paths(s: &str) -> Vec<PathBuf> {
    let sep = if cfg!(windows) { ';' } else { ':' };
    s.split(sep)
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .map(PathBuf::from)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    /// Build a fake env getter from a static map — avoids global env-var
    /// race when cargo runs tests in parallel. Returns owned strings so
    /// the closure can outlive the input slice.
    fn fake_env(pairs: &[(&str, &str)]) -> impl Fn(&str) -> Option<String> {
        let map: HashMap<String, String> = pairs.iter()
            .map(|(k, v)| ((*k).to_string(), (*v).to_string()))
            .collect();
        move |name: &str| map.get(name).cloned()
    }

    #[test]
    fn known_knobs_alphabetical_and_unique() {
        let names: Vec<&str> = KNOWN_KNOBS.iter().map(|k| k.name).collect();
        let mut sorted = names.clone();
        sorted.sort();
        assert_eq!(names, sorted, "KNOWN_KNOBS must be alphabetically sorted by name");

        let mut uniq = sorted.clone();
        uniq.dedup();
        assert_eq!(uniq.len(), sorted.len(), "KNOWN_KNOBS contains duplicate names");
    }

    #[test]
    fn known_knobs_match_struct_fields_for_startup_knobs() {
        // The 4 path-ish fields on RuntimeConfig must each appear in KNOWN_KNOBS.
        let names: Vec<&str> = KNOWN_KNOBS.iter().map(|k| k.name).collect();
        for required in ["Z42_LIBS", "Z42_PATH", "Z42_LOG", "Z42_CRASH_DIR"] {
            assert!(names.contains(&required),
                "RuntimeConfig field expects {required} in KNOWN_KNOBS");
        }
    }

    #[test]
    fn from_getter_all_unset() {
        let cfg = RuntimeConfig::from_getter(fake_env(&[]));
        assert!(cfg.libs_dir.is_none());
        assert!(cfg.log_filter.is_none());
        assert!(cfg.crash_dir.is_none());
        assert!(cfg.module_path.is_empty());
    }

    #[test]
    fn from_getter_empty_string_is_unset() {
        let cfg = RuntimeConfig::from_getter(fake_env(&[("Z42_LIBS", "")]));
        assert!(cfg.libs_dir.is_none(), "empty Z42_LIBS should be treated as unset");
    }

    #[test]
    fn from_getter_whitespace_only_is_unset() {
        let cfg = RuntimeConfig::from_getter(fake_env(&[("Z42_LOG", "   ")]));
        assert!(cfg.log_filter.is_none(), "whitespace-only Z42_LOG should be unset");
    }

    #[test]
    fn from_getter_libs_set() {
        let cfg = RuntimeConfig::from_getter(fake_env(&[("Z42_LIBS", "/tmp/z42-libs")]));
        assert_eq!(cfg.libs_dir.as_deref(), Some(std::path::Path::new("/tmp/z42-libs")));
    }

    #[test]
    fn from_getter_path_splits_on_platform_separator() {
        let sep = if cfg!(windows) { ';' } else { ':' };
        let input = format!("/a{sep}/b{sep}/c");
        let cfg = RuntimeConfig::from_getter(fake_env(&[("Z42_PATH", &input)]));
        assert_eq!(
            cfg.module_path,
            vec![PathBuf::from("/a"), PathBuf::from("/b"), PathBuf::from("/c")]
        );
    }

    #[test]
    fn from_getter_path_skips_empty_segments() {
        let sep = if cfg!(windows) { ';' } else { ':' };
        let input = format!("/a{sep}{sep}/b{sep} {sep} /c");
        let cfg = RuntimeConfig::from_getter(fake_env(&[("Z42_PATH", &input)]));
        assert_eq!(
            cfg.module_path,
            vec![PathBuf::from("/a"), PathBuf::from("/b"), PathBuf::from("/c")]
        );
    }

    #[test]
    fn from_getter_log_filter_passes_through() {
        let cfg = RuntimeConfig::from_getter(fake_env(&[("Z42_LOG", "z42::jit=debug,z42=warn")]));
        assert_eq!(cfg.log_filter.as_deref(), Some("z42::jit=debug,z42=warn"));
    }

    #[test]
    fn from_getter_crash_dir_set() {
        let cfg = RuntimeConfig::from_getter(fake_env(&[("Z42_CRASH_DIR", "/var/log/z42")]));
        assert_eq!(cfg.crash_dir.as_deref(), Some(std::path::Path::new("/var/log/z42")));
    }

    #[test]
    fn from_getter_ignores_unrelated_env_vars() {
        let cfg = RuntimeConfig::from_getter(fake_env(&[
            ("Z42_NATIVE_PATH", "/some/native"),  // subsystem-local; not in struct
            ("RUST_BACKTRACE", "1"),               // unrelated env
        ]));
        assert!(cfg.libs_dir.is_none());
        assert!(cfg.log_filter.is_none());
    }
}
