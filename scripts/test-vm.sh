#!/usr/bin/env bash
# scripts/test-vm.sh — bootstrap stub for the z42 implementation.
#
# 2026-05-27 port-test-vm: 主体迁移到 scripts/test-vm.z42.
# bash stub 负责 self-host 边界 + heavy toolchain build：
#   1. cd to repo root, parse legacy CLI (mode positional, --no-rebuild, --jobs)
#   2. Default: invoke regen-golden-tests.sh (which transitively rebuilds
#      compiler + stdlib + .zbc artifacts) to avoid stale-artifact GREEN/RED
#      (fix-test-vm-stale-artifacts 2026-05-04 fixed silent stale-zbc bug)
#   3. Build z42vm + z42-compression cdylib (JIT pre-resolves all builtins
#      at compile time, so missing the compressor dylib panics every JIT test)
#   4. Exec scripts/test-vm.z42 with mode list + jobs passed via env vars
#      (driver `run` subcommand doesn't forward arbitrary args to scripts)
#
# Usage:
#   ./scripts/test-vm.sh                       # both modes, sequential
#   ./scripts/test-vm.sh interp                # interp only
#   ./scripts/test-vm.sh jit                   # jit only
#   ./scripts/test-vm.sh --no-rebuild          # skip stdlib + zbc rebuild
#   ./scripts/test-vm.sh --jobs=8              # 8 parallel workers
#   ./scripts/test-vm.sh interp --no-rebuild --jobs=4
#
# Exit code: 0 if all selected tests pass, 1 otherwise.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

# ── Arg parsing ──────────────────────────────────────────────────────────
MODES="interp jit"
REBUILD=true
JOBS=1
POSITIONAL=()
for arg in "$@"; do
    case "$arg" in
        --no-rebuild)  REBUILD=false ;;
        --jobs=*)      JOBS="${arg#--jobs=}" ;;
        --jobs|-j)     echo "error: --jobs requires a value (e.g. --jobs=4)" >&2; exit 2 ;;
        -j[0-9]*)      JOBS="${arg#-j}" ;;
        *)             POSITIONAL+=("$arg") ;;
    esac
done
if [ "${#POSITIONAL[@]}" -ge 1 ]; then
    MODES="${POSITIONAL[0]}"
fi

# ── Stdlib + golden .zbc rebuild ─────────────────────────────────────────
if [ "$REBUILD" = true ]; then
    "$SCRIPT_DIR/regen-golden-tests.sh"
    echo ""
fi

# ── VM + compressor cdylib build ─────────────────────────────────────────
# Direct binary path (target-dir set via .cargo/config.toml). Using the
# binary directly (not `cargo run`) avoids per-test cargo overhead
# (~100 ms / invocation) and eliminates cargo-lock contention in parallel.
echo "Building VM..."
cargo build -q --manifest-path src/runtime/Cargo.toml
cargo build -q --manifest-path src/runtime/crates/z42-compression/Cargo.toml
VM_BIN="$ROOT/artifacts/build/runtime/debug/z42vm"
if [[ ! -x "$VM_BIN" ]]; then
    echo "error: VM binary not found at $VM_BIN after cargo build" >&2
    exit 1
fi
echo ""

# ── Hand off ─────────────────────────────────────────────────────────────
exec env Z42_VM_MODES="$MODES" Z42_VM_JOBS="$JOBS" \
    dotnet run --project src/compiler/z42.Driver --verbosity quiet --no-build -- \
        run scripts/test-vm.z42
