#!/usr/bin/env bash
# scripts/regen-golden-tests.sh — Recompile all golden test source.z42 → source.zbc.
#
# Run this after compiler changes to regenerate the VM golden test artifacts.
# The resulting source.zbc files are checked into the repo and used by test-vm.sh.
#
# Default behaviour rebuilds the stdlib zpkgs first (via build-stdlib.sh) and
# syncs them to artifacts/z42/libs/. This guarantees user-facing golden sources
# that import stdlib types compile against the latest stdlib IR / signatures
# (2026-05-04 fix-test-vm-stale-artifacts: prior independent invocations could
# silently emit zbc against stale stdlib because nothing forced a sync).
#
# Usage:
#   ./scripts/regen-golden-tests.sh                  # debug build, rebuild stdlib
#   ./scripts/regen-golden-tests.sh release          # release build
#   ./scripts/regen-golden-tests.sh --no-stdlib      # skip stdlib rebuild
#   ./scripts/regen-golden-tests.sh release --no-stdlib

set -euo pipefail

# Resolve project root regardless of working directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

GOLDEN_GLOBS=(
    "src/runtime/tests/golden/run/*/"
    "src/libraries/"*"/tests/golden/"*"/"
)
COMPILER_SLN="src/compiler/z42.slnx"

BUILD_CONFIG="Debug"
REBUILD_STDLIB=true
for arg in "$@"; do
    case "$arg" in
        release)      BUILD_CONFIG="Release" ;;
        --no-stdlib)  REBUILD_STDLIB=false ;;
    esac
done

if [ "$REBUILD_STDLIB" = true ]; then
    # build-stdlib.sh internally invokes the C# compiler (dotnet run --project
    # z42.Driver), which transitively rebuilds the compiler if stale. After
    # producing dist/<lib>.zpkg it syncs into artifacts/z42/libs/ so the VM
    # loader sees the current stdlib. Skip via --no-stdlib when the caller has
    # already done this (e.g. test-vm.sh re-entry).
    "$SCRIPT_DIR/build-stdlib.sh"
    echo ""
fi

# Build the compiler first.
echo "Building compiler (${BUILD_CONFIG})..."
dotnet build -q "$COMPILER_SLN" -c "$BUILD_CONFIG"

# Locate the driver DLL (output name is z42c.dll per z42.Driver.csproj).
DRIVER_DLL="artifacts/compiler/z42.Driver/bin/z42c.dll"
if [ ! -f "$DRIVER_DLL" ]; then
    echo "error: driver DLL not found at $DRIVER_DLL"
    exit 1
fi

echo "Regenerating golden test artifacts..."
echo ""

PASS=0
FAIL=0
SKIP=0
FAILURES=()

DIRS=()
for glob in "${GOLDEN_GLOBS[@]}"; do
    for d in $glob; do
        [ -d "$d" ] && DIRS+=("$d")
    done
done

for dir in "${DIRS[@]}"; do
    name=$(basename "$dir")
    source="$dir/source.z42"
    output="$dir/source.zbc"

    if [ ! -f "$source" ]; then
        echo "  SKIP: $name (no source.z42)"
        SKIP=$((SKIP + 1))
        continue
    fi

    if dotnet "$DRIVER_DLL" "$source" --emit zbc -o "$output" 2>/dev/null; then
        echo "  OK:   $name"
        PASS=$((PASS + 1))
    else
        echo "  FAIL: $name"
        FAIL=$((FAIL + 1))
        FAILURES+=("$name")
    fi
done

echo ""
echo "══════════════════════════════════════"
echo " Regenerated: $PASS ok, $FAIL failed, $SKIP skipped"
echo "══════════════════════════════════════"

if [ "${#FAILURES[@]}" -gt 0 ]; then
    echo ""
    echo "Failed tests:"
    for f in "${FAILURES[@]}"; do
        echo "  - $f"
    done
    exit 1
fi
