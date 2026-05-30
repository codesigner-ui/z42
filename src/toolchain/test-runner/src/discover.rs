//! Test discovery from compile-time TIDX section.
//!
//! Phase: rewrite-z42-test-runner-compile-time S1 (2026-05-10) — extracted
//! from monolithic `main.rs`.
//!
//! Walks `LoadedArtifact.test_index` (R1 add-test-metadata-section), filters
//! out [Ignore]-marked entries, materializes [Skip] reason and
//! [ShouldThrow<E>] expected type per (R4.B / A2 / A3 chain expansion).

use z42::metadata::{LoadedArtifact, TestEntryKind, TestFlags};

#[allow(dead_code)] // method_id reserved for full-impl filtering
pub struct DiscoveredTest<'a> {
    pub method_id: u32,
    pub method_name: &'a str,
    pub flags: TestFlags,
    /// User-written `reason:` arg from `[Skip(reason: "...")]` — surfaced
    /// verbatim. Decoupled from `skip_platform` / `skip_feature` so the
    /// runner's conditional evaluator can compose its own "skipped on X
    /// because Y" message.
    pub skip_reason: Option<String>,
    /// User-written `platform:` arg from `[Skip(platform: "ios")]` — the
    /// runner skips only when this matches the current host platform.
    /// add-test-skip-platform-feature-eval (2026-05-30): semantic now
    /// actually evaluated (previously emitted but ignored).
    pub skip_platform: Option<String>,
    /// User-written `feature:` arg from `[Skip(feature: "jit")]` — the
    /// runner skips only when this feature is not in its capability set
    /// (`SkipEnv.available_features`). Deny-by-default for unknown names.
    pub skip_feature: Option<String>,
    /// Resolved [ShouldThrow<E>] expected exception type name (R4.B / A2).
    /// Populated only when SHOULD_THROW flag is set and the type was resolved.
    /// `None` means the test has no ShouldThrow expectation.
    pub expected_throw: Option<String>,
    /// add-test-timeout-attribute (2026-05-30): per-test wallclock budget
    /// override from `[Timeout(milliseconds: N)]`. `None` = no override
    /// (runner applies its built-in default). `Some(ms)` = explicit cap.
    pub timeout_ms: Option<u32>,
}

pub struct TestReport<'a> {
    pub tests: Vec<DiscoveredTest<'a>>,
}

impl<'a> TestReport<'a> {
    pub fn from_artifact(artifact: &'a LoadedArtifact) -> Self {
        let mut tests = Vec::new();
        for entry in &artifact.test_index {
            if entry.kind != TestEntryKind::Test { continue; }
            if entry.flags.contains(TestFlags::IGNORED) {
                // [Ignore] — silently omit per design.
                continue;
            }
            let method = &artifact.module.functions[entry.method_id as usize];
            // add-test-skip-platform-feature-eval (2026-05-30): three skip
            // segments are kept independent so the runtime evaluator can
            // decide *whether* to skip based on platform/feature match;
            // string composition moves into `skip_eval::format_reason`.
            let (skip_reason, skip_platform, skip_feature) =
                if entry.flags.contains(TestFlags::SKIPPED) {
                    (
                        entry.skip_reason.clone(),
                        entry.skip_platform.clone(),
                        entry.skip_feature.clone(),
                    )
                } else {
                    (None, None, None)
                };
            // R4.B / A2 — only populate when SHOULD_THROW is set AND the type
            // resolved (str_idx == 0 leaves expected_throw_type as None).
            let expected_throw = if entry.flags.contains(TestFlags::SHOULD_THROW) {
                entry.expected_throw_type.clone()
            } else {
                None
            };
            tests.push(DiscoveredTest {
                method_id: entry.method_id,
                method_name: &method.name,
                flags: entry.flags,
                skip_reason,
                skip_platform,
                skip_feature,
                expected_throw,
                // add-test-timeout-attribute (2026-05-30): 0 on-wire = None
                // (use runner default); positive = Some(N) per-test override.
                timeout_ms: if entry.timeout_ms == 0 { None } else { Some(entry.timeout_ms) },
            });
        }
        Self { tests }
    }
}
