# Spec: Test runner — conditional skip evaluation

## ADDED Requirements

### Requirement: `[Skip(platform:)]` — host-platform-conditional skip

#### Scenario: platform matches current host
- **WHEN** a test carries `[Skip(platform: "linux", reason: "x")]` and runner is invoked on linux
- **THEN** `Outcome::Skipped { reason }` is produced
- **AND** the reason string contains `"on linux"` and includes the user's reason `"x"`

#### Scenario: platform differs from current host
- **WHEN** a test carries `[Skip(platform: "ios", reason: "iOS-only WebGL bug")]` and runner is invoked on linux
- **THEN** the test runs normally (no `Outcome::Skipped`)
- **AND** the outcome is whatever the body produces

#### Scenario: CLI `--platform <NAME>` overrides host detection
- **WHEN** runner is invoked on linux with `--platform ios` and the test carries `[Skip(platform: "ios")]`
- **THEN** the test is skipped (override takes precedence over `std::env::consts::OS`)

#### Scenario: env var `Z42_TEST_PLATFORM` overrides host detection
- **WHEN** runner is invoked with `Z42_TEST_PLATFORM=ios` and no `--platform` CLI flag, on linux host
- **THEN** the test carrying `[Skip(platform: "ios")]` is skipped

#### Scenario: CLI takes precedence over env var
- **WHEN** runner is invoked with `Z42_TEST_PLATFORM=ios --platform linux`
- **THEN** decision uses `linux` (CLI wins)

### Requirement: `[Skip(feature:)]` — runtime-capability-conditional skip

#### Scenario: feature is available
- **WHEN** a test carries `[Skip(feature: "filesystem")]` and runner runs on a non-wasm build (filesystem available)
- **THEN** the test runs normally (no skip)

#### Scenario: feature is unavailable
- **WHEN** a test carries `[Skip(feature: "filesystem")]` and runner runs on wasm32 (filesystem absent)
- **THEN** the test is skipped
- **AND** reason contains `"feature 'filesystem' unavailable"`

#### Scenario: feature name is unknown
- **WHEN** a test carries `[Skip(feature: "quantum_entanglement")]` (not in registry)
- **THEN** the test is skipped (deny-by-default for unknown features)
- **AND** stderr emits one-line `note: unknown feature "quantum_entanglement" — treating as unavailable`

### Requirement: compound skip — OR semantics

#### Scenario: platform matches OR feature unavailable → skip
- **WHEN** a test carries `[Skip(platform: "ios", feature: "jit")]`
- **AND** runner runs on ios (platform matches) but jit is available
- **THEN** the test is skipped

#### Scenario: platform mismatch AND feature available → run
- **WHEN** a test carries `[Skip(platform: "ios", feature: "jit")]`
- **AND** runner runs on linux (platform mismatch) and jit is available
- **THEN** the test runs normally

#### Scenario: compound reason string
- **WHEN** compound skip triggers because both platform matches AND feature unavailable
- **THEN** reason contains `"on <platform>; feature '<name>' unavailable"`

### Requirement: unconditional skip preserved

#### Scenario: `[Skip(reason: "...")]` without platform/feature
- **WHEN** a test carries `[Skip(reason: "broken until next sprint")]` with no platform / feature
- **THEN** the test is skipped on every host
- **AND** reason equals the user's reason text (no "on <platform>" prefix)

#### Scenario: bare `[Skip]` with no args at all
- **WHEN** a test carries `[Skip]` (no named args)
- **THEN** the test is skipped on every host
- **AND** reason is `"skipped"` (placeholder)

#### Scenario: test without `[Skip]` flag
- **WHEN** a test has no SKIPPED flag (no `[Skip]` attribute)
- **THEN** skip evaluator returns `None` regardless of any other state — test runs normally

## MODIFIED Requirements

### Requirement: `DiscoveredTest` shape (discover.rs)

**Before:** `DiscoveredTest.skip_reason: Option<String>` is a pre-formatted composite string
containing `"platform=X; feature=Y; reason"` segments, built by `format_skip_reason()`.
All consumers treat it as opaque display text; conditional logic is impossible.

**After:** `DiscoveredTest` exposes three independent fields:

```rust
pub skip_reason   : Option<String>,  // user-written reason: only
pub skip_platform : Option<String>,  // user-written platform: only
pub skip_feature  : Option<String>,  // user-written feature: only
```

`format_skip_reason()` is removed; the human-readable "why skipped" message is now
synthesized inside `skip_eval::decide_skip` after evaluation succeeds and includes
the **triggered** condition (platform-match or feature-unavailability) for clarity.

## Pipeline Steps

Receiving impact (which stage of the test-runner pipeline changes):
- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [ ] VM interp
- [x] **Test runner — discovery (`discover.rs` field shape)**
- [x] **Test runner — execution (`runner.rs` + `exec.rs` skip branch)**
- [x] **Test runner — scheduling (`main.rs` SkipEnv construction)**

No compiler-side changes — TIDX fields already exist (R1.C); semantic is shifted
runtime-side.
