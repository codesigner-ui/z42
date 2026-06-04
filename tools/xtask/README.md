# xtask — z42 repo dev CLI

The repo's unified build/test/dev CLI, a z42 program (`xtask.zpkg`) run via the
launcher. NOT part of the general `z42` launcher (which stays a generic runtime).

## Build + run
```
z42c build tools/xtask/xtask.z42.toml --release   # → artifacts/build/toolchain/xtask/xtask.zpkg
z42 xtask.zpkg <command> [args]                    # run via launcher
```

## Commands
- `build runtime|compiler|stdlib [lib]|launcher|package [--rid R]|all`
- `test (empty=all)|stdlib [lib]|vm [interp|jit]|cross-zpkg|compiler|changed` (+ `--parallel`, `--jobs N`)
- `run <target> [-- args]` · `deps check|install` · `bench [--diff]` · `help`

## Status
- **Stream 3 (add-xtask-cli):** most subcommands cd to the repo root and
  **delegate to the existing script/.z42** via `_sh` (bash subprocess).
- **Stream 2 (migrate-scripts-to-z42), incremental:** subcommands are being
  moved to native, **bash-free** orchestration — `_exec(Process)` spawns
  cargo/dotnet/git/z42vm directly (`Process.WorkingDirectory`), `_root()` finds
  the repo root via `git`, and compiled `.z42` logic runs on this process's own
  inherited z42vm (`Z42_PORTABLE_VM` / `Z42_HOME`). The corresponding `.sh` is
  kept as fallback until each native path is CI-proven, then deleted.
  - ✅ `deps check` — native (compiles + runs check-versions-drift.z42, no bash)
  - ✅ `test changed` — native outer (runs test-changed.z42 via the driver, base/
    --dry-run via env vars); inner `just` command-exec is a tracked follow-up
