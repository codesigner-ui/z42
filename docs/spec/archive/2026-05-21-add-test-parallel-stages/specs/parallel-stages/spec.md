# Spec: Parallel stage execution

## ADDED Requirements

### Requirement: `--parallel` runs stages in dependency-respecting waves

#### Scenario: scope=full with --parallel uses 3 waves
- **WHEN** `./scripts/test-all.sh --scope=full --parallel`
- **THEN** executes 3 waves sequentially:
  1. dotnet build + cargo build (parallel)
  2. dotnet test + test-stdlib (parallel)
  3. test-vm --no-rebuild + test-cross-zpkg (parallel)
- **AND** total wall time is roughly `max(W1) + max(W2) + max(W3)`,
  not the sum of all 6 stage times

#### Scenario: scope=runtime with --parallel skips dotnet waves
- **WHEN** `./scripts/test-all.sh --scope=runtime --parallel`
- **THEN** executes:
  1. cargo build (alone — no dotnet build in scope)
  2. test-stdlib (alone — dotnet test skipped)
  3. test-vm --no-rebuild + test-cross-zpkg (parallel)

#### Scenario: scope=stdlib with --parallel collapses to mostly serial
- **WHEN** `./scripts/test-all.sh --scope=stdlib --parallel`
- **THEN** executes:
  1. test-stdlib (no build stages in scope)
  2. test-vm --no-rebuild + test-cross-zpkg (parallel)

### Requirement: --parallel implies --no-rebuild for test-vm

#### Scenario: Avoids stdlib-rebuild race
- **WHEN** `--parallel` is set, test-vm in Wave 3 is invoked with
  `--no-rebuild`
- **THEN** test-vm uses the stdlib zpkgs built by Wave 2's test-stdlib;
  no race on `artifacts/build/libs/`

### Requirement: Captured output prints in wave-stage order

#### Scenario: Parallel stage output stays readable
- **WHEN** 2 stages run in parallel within a wave
- **THEN** each stage's stdout/stderr is captured to a temp file;
  after `wait` completes, the temp files are printed in original
  stage-list order; user sees the same content as serial mode (no
  interleaving)

### Requirement: Wave failure stops subsequent waves

#### Scenario: Wave 1 failure → Wave 2 + 3 skipped
- **WHEN** dotnet build fails in Wave 1
- **THEN** the script reports the failure (with the failing stage name)
  and exits 1 without running Wave 2 or 3

## MODIFIED Requirements

### Requirement: `test-all.sh` flag set

**Before:** `--scope=<value>`, `--with-dist`, `--quick`

**After:** Adds `--parallel`. Composable with all existing flags.

## IR Mapping

No code change. Shell script + docs only.

## Pipeline Steps

- [ ] None affected
