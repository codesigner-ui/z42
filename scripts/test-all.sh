#!/usr/bin/env bash
# test-all.sh — Run every check the workflow's GREEN definition requires.
#
# Single entry point that fans out into the 4 existing per-area scripts so
# you can't accidentally skip one (the cross-zpkg-catch regression hid behind
# test-stdlib not being in the default GREEN path).
#
# Stages (each must pass; first failing stage stops the run):
#   1. dotnet build            — compiler compiles
#   2. cargo build (release)   — runtime compiles
#   3. dotnet test             — compiler unit tests (1233+)
#   4. test-vm.sh              — VM goldens interp + JIT (320+)
#   5. test-cross-zpkg.sh      — cross-package metadata e2e
#   6. test-stdlib.sh          — stdlib [Test] dogfood (6 libs)
#
# Optional stages (skipped unless explicitly requested):
#   7. test-dist.sh            — packaged binary e2e (--with-dist; requires
#                                ./scripts/package.sh release run beforehand)
#
# Usage:
#   ./scripts/test-all.sh                 # required stages 1-6
#   ./scripts/test-all.sh --with-dist     # also run packaged binary check
#   ./scripts/test-all.sh --quick         # skip rebuild steps (faster iter)
#
# Exit code: 0 if every selected stage passes, 1 otherwise (with a one-line
# failure summary). Pass-through stdout from each stage stays visible so
# CI logs read the same as running each script individually.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

WITH_DIST=false
QUICK=false
for arg in "$@"; do
    case "$arg" in
        --with-dist) WITH_DIST=true ;;
        --quick)     QUICK=true ;;
        -h|--help)
            sed -n '2,/^set -euo/p' "$0" | sed 's/^# \{0,1\}//;s/^#$//;/^set -euo/d'
            exit 0 ;;
        *) echo "unknown arg: $arg (try --help)" >&2; exit 2 ;;
    esac
done

# Each stage is "name|command"; commands run in `bash -c` so we can chain.
STAGES=(
    "dotnet build|dotnet build src/compiler/z42.slnx --nologo -v quiet"
    "cargo build (release)|cargo build --manifest-path src/runtime/Cargo.toml --release --quiet"
    "dotnet test|dotnet test src/compiler/z42.Tests/z42.Tests.csproj --nologo"
    "VM goldens|./scripts/test-vm.sh $($QUICK && echo '--no-rebuild' || true)"
    "cross-zpkg|./scripts/test-cross-zpkg.sh"
    "stdlib [Test]|./scripts/test-stdlib.sh"
)

if [ "$WITH_DIST" = true ]; then
    STAGES+=("packaged binary|./scripts/test-dist.sh")
fi

passed=()
failed=""
for entry in "${STAGES[@]}"; do
    IFS='|' read -r name cmd <<< "$entry"
    echo ""
    echo "════════════════════════════════════════════════"
    echo "  $name"
    echo "════════════════════════════════════════════════"
    if bash -c "$cmd"; then
        passed+=("$name")
    else
        failed="$name"
        break
    fi
done

echo ""
echo "════════════════════════════════════════════════"
if [ -n "$failed" ]; then
    echo "  ❌ FAILED at: $failed"
    echo "  (${#passed[@]}/${#STAGES[@]} stages passed before failure)"
    echo "════════════════════════════════════════════════"
    exit 1
fi
echo "  ✅ ALL GREEN (${#STAGES[@]} stages)"
echo "════════════════════════════════════════════════"
