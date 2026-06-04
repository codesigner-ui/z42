# Proposal: add-xtask-cli — unified repo dev CLI (xtask.zpkg)

## Why
The repo's build/test/dev flows are ~18 separate scripts. Per the self-hosting
plan, they unify into ONE z42 program — `xtask.zpkg` — run via the launcher:
`z42 xtask.zpkg build|test|run|deps …`. This is the repo's build-flow CLI (NOT
baked into the general launcher, which stays a generic runtime). Stream 3
establishes the CLI skeleton + dispatch + help; Stream 2 (migrate-scripts-to-z42)
ports each script's logic into the subcommands and deletes the `.sh`.

## What Changes
- NEW `tools/xtask/xtask.z42` + `xtask.z42.toml` (kind=exe) → `xtask.zpkg`.
- Subcommands: `build` (runtime/compiler/stdlib/launcher/package/all),
  `test` (all/stdlib/vm/cross-zpkg/compiler/changed), `run`, `deps`
  (check/install), `bench`, plus `help`. Global `-h/--help`.
- MVP dispatch: each subcommand delegates to the existing script/.z42 via a
  subprocess (Std.IO.Process), forwarding stdout/stderr + exit code, after
  cd-ing to repo root. Stream 2 replaces each delegation with native z42.
- Full `--help` text (the unified CLI reference).

## Scope
| 文件 | 类型 | 说明 |
|------|------|------|
| `tools/xtask/xtask.z42` | NEW | CLI dispatcher + handlers + help |
| `tools/xtask/xtask.z42.toml` | NEW | kind=exe project → artifacts/build/toolchain/xtask/xtask.zpkg |
| `tools/xtask/README.md` | NEW | invocation + subcommand map |
| `docs/design/compiler/build-artifacts-layout.md` | MODIFY | build/toolchain/xtask 注记 |

## Out of Scope
- Porting each script's logic into native z42 (Stream 2: migrate-scripts-to-z42).
- Deleting any .sh (waits for new nightly + Stream 2).
- A root `./x` wrapper (decided: use `z42 xtask.zpkg …`).
