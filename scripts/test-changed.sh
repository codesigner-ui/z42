#!/usr/bin/env bash
# scripts/test-changed.sh — bootstrap stub for the z42 implementation.
#
# 2026-05-27 port-test-changed: 主体迁移到 scripts/test-changed.z42.
# bash stub:
#   1. cd to repo root, parse legacy CLI (base ref positional, --dry-run flag)
#   2. Ensure compiler driver is built (single-project build to avoid MSB3492)
#   3. Pass base + dry-run via env vars (driver `run` doesn't forward script args)
#   4. Exec scripts/test-changed.z42 to do the actual git diff + plan + run
#
# Usage:
#   ./scripts/test-changed.sh                # base = HEAD
#   ./scripts/test-changed.sh main           # base = main
#   ./scripts/test-changed.sh --dry-run
#   Z42_TEST_CHANGED_BASE=origin/main ./scripts/test-changed.sh
#
# Exit codes:
#   0  — all selected commands passed (or none to run)
#   N  — first failing command's exit code (passed through)
#   2  — tool error (not a git repo, git diff failed)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

DRY_RUN=0
BASE_ARG=""
for arg in "$@"; do
    case "$arg" in
        --dry-run) DRY_RUN=1 ;;
        -*)        echo "[test-changed] unknown flag: $arg" >&2; exit 2 ;;
        *)         BASE_ARG="$arg" ;;
    esac
done

BASE="${Z42_TEST_CHANGED_BASE:-${BASE_ARG:-HEAD}}"

# Ensure z42c driver exists. Pre-existing cargo / dotnet builds in the
# workspace usually mean this is a no-op. Single-project build (not slnx)
# to avoid MSB3492 with concurrent CodeCoverage targets.
DRIVER_DLL="artifacts/build/compiler/z42.Driver/bin/z42c.dll"
if [ ! -f "$DRIVER_DLL" ]; then
    dotnet build -q src/compiler/z42.Driver/z42.Driver.csproj
fi

# Hand off to the z42 implementation.
exec env Z42_TEST_CHANGED_BASE="$BASE" Z42_TEST_CHANGED_DRY_RUN="$DRY_RUN" \
    dotnet run --project src/compiler/z42.Driver --verbosity quiet --no-build -- \
        run scripts/test-changed.z42
