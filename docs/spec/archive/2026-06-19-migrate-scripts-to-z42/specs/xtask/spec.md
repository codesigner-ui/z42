# Spec: xtask native orchestration (bash-free)

## ADDED Requirements

### Requirement: bash-free process orchestration in xtask
#### Scenario: spawn a binary directly
- WHEN xtask runs a toolchain step (cargo / dotnet / git / z42vm)
- THEN it spawns the binary via Std.IO.Process with WorkingDirectory set to the
  repo root, NOT via `bash -c`, forwarding stdout/stderr + exit code

#### Scenario: locate repo root without bash
- WHEN xtask needs the repo root
- THEN it runs `git rev-parse --show-toplevel` (git spawned directly) and uses
  its stdout, falling back to the current directory

### Requirement: `deps check` runs natively
#### Scenario: consistent versions
- WHEN `z42 xtask.zpkg deps check` and versions.toml projections are consistent
- THEN xtask compiles check-versions-drift.z42 and runs the zpkg on its inherited
  runtime (Z42_PORTABLE_VM / Z42_LIBS), exit 0 — same as check-versions-drift.sh
#### Scenario: drift present
- WHEN a projection drifts from versions.toml
- THEN exit 1 with the same mismatch report

## Note
check-versions-drift.sh is RETAINED as fallback; CI is unchanged in this
increment. Rewire + delete only after the native path is CI-proven.
