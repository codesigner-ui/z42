# Proposal: migrate-scripts-to-z42

## Why
The `scripts/*.sh` files are the last non-z42 asset in the toolchain. Most are
already thin **bootstrap stubs** that build the toolchain (the self-host
boundary) then `exec` a native `.z42` implementation. Now that (1) `xtask.zpkg`
is the unified dev CLI and (2) the nightly ships a prebuilt `z42` launcher
(install-z42), xtask — running on that launcher — can orchestrate the toolchain
build by spawning `cargo`/`dotnet`/`git` **directly via `Std.IO.Process`
(no bash)** and run compiled `.z42` logic on its own inherited runtime
(`Z42_PORTABLE_VM` / `Z42_LIBS`). This unblocks a bash-free dev/CI flow on
Windows.

## What Changes
Incrementally move each subsystem's orchestration from a `.sh` stub into a
native xtask handler. Per increment: (a) add the native path in xtask, spawning
toolchain binaries directly (no `bash -c`); (b) validate it matches the `.sh`
locally; (c) **keep the `.sh` as fallback**; (d) only after the native path is
CI-proven through one nightly cycle, rewire CI to call `z42 xtask.zpkg …` and
delete the `.sh`.

Self-host boundary that REMAINS: compiling `.z42` needs `z42c` (the C# driver,
hence `dotnet`); building the VM needs `cargo`. xtask spawns these directly —
it removes bash, not the compilers.

## Scope (grows per increment; increment 1 only listed concretely)
| File | Change | Increment |
|------|--------|-----------|
| `scripts/xtask.z42` | MODIFY — native `_run` (no bash) + `_root` via git; `deps check` runs check-versions-drift natively | 1 |
| `scripts/check-versions-drift.sh` | KEEP (fallback) | 1 |
| `docs/design/compiler/build-artifacts-layout.md` | MODIFY — note xtask native orchestration | 1 |
| (subsequent) `scripts/xtask.z42` + each `.sh` | per-increment | 2..N |

## Out of Scope
- Deleting any `.sh` in increment 1 (fallback retained until CI-proven).
- `package_*.sh` platform subs that wrap vendor tools (xcodebuild/gradle/lipo):
  xtask will *orchestrate* them but the vendor-tool shell-outs stay where they
  must — flagged for per-increment decision.
- `setup-tools.sh` / `install-*.sh` toolchain bootstrap (run before any z42).

## Open Questions
- [ ] Order after increment 1: `deps check` → `test changed` → `test vm` →
      `test cross-zpkg` → `build stdlib` → `test all` (biggest last).
