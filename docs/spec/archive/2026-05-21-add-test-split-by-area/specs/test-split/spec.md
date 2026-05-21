# Spec: Scope-aware test-all.sh

## ADDED Requirements

### Requirement: `--scope=runtime` skips compiler stages

#### Scenario: Runtime-only change skips dotnet build + dotnet test
- **WHEN** running `./scripts/test-all.sh --scope=runtime`
- **THEN** the executed stages are exactly: cargo build, test-vm,
  test-cross-zpkg, test-stdlib (dotnet build + dotnet test SKIPPED)
- **AND** stage count in the final summary line reflects only the
  executed count (e.g. "✅ ALL GREEN (4 stages)")

### Requirement: `--scope=compiler` skips cargo build

#### Scenario: Compiler-only change skips runtime rebuild
- **WHEN** running `./scripts/test-all.sh --scope=compiler`
- **THEN** the executed stages are: dotnet build, dotnet test, test-vm,
  test-cross-zpkg, test-stdlib (cargo build SKIPPED)
- **AND** the cached runtime binary is used by test-vm / test-stdlib

### Requirement: `--scope=stdlib` skips both build + compiler test stages

#### Scenario: Pure .z42 stdlib edit skips toolchain rebuilds
- **WHEN** running `./scripts/test-all.sh --scope=stdlib`
- **THEN** the executed stages are: test-vm, test-cross-zpkg, test-stdlib
  (both build + dotnet test SKIPPED)
- **AND** test-stdlib re-uses the already-built compiler + runtime

### Requirement: `--scope=full` (default) is current behavior

#### Scenario: No flag runs the full pipeline
- **WHEN** running `./scripts/test-all.sh` (no `--scope` arg)
- **THEN** runs all 6 stages identical to pre-spec behavior

### Requirement: `--scope=auto` detects scope from git diff

#### Scenario: Auto-detect picks narrowest scope covering all touched files
- **WHEN** running `./scripts/test-all.sh --scope=auto` with uncommitted
  changes touching only `src/runtime/**`
- **THEN** auto-detect resolves to `runtime` scope (skips dotnet stages)

#### Scenario: Mixed change resolves to full
- **WHEN** uncommitted changes touch both `src/compiler/**` and
  `src/runtime/**`
- **THEN** auto-detect resolves to `full` (no skipping)

#### Scenario: Unknown / outside-scope files fall back to full
- **WHEN** uncommitted changes touch only `Cargo.toml` or some path not
  classified
- **THEN** auto-detect resolves to `full` (safe fallback)

## MODIFIED Requirements

### Requirement: GREEN gate documentation (workflow.md 阶段 8)

**Before:** Single `./scripts/test-all.sh` invocation = GREEN.

**After:** Scope-aware invocation. Iteration uses `--scope=runtime|compiler|stdlib|auto` for speed; **the final pre-commit GREEN must be `--scope=full`** (or `--scope=auto` not narrower than the touched set) — partial scope coverage is NOT acceptable as the GREEN gate.

## IR Mapping

No code change. Shell script + docs only.

## Pipeline Steps

- [ ] None affected
