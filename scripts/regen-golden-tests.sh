#!/usr/bin/env bash
# scripts/regen-golden-tests.sh — Recompile all golden test source.z42 → source.zbc.
#
# Run this after compiler changes to regenerate the VM golden test artifacts.
# The resulting source.zbc files are checked into the repo and used by test-vm.sh.
#
# Usage:
#   ./scripts/regen-golden-tests.sh          # debug build (default)
#   ./scripts/regen-golden-tests.sh release  # release build

set -euo pipefail

GOLDEN_DIR="src/runtime/tests/golden/run"
COMPILER_SLN="src/compiler/z42.slnx"

BUILD_CONFIG="Debug"
if [ "${1:-}" = "release" ]; then
    BUILD_CONFIG="Release"
fi

# Build the compiler first.
echo "Building compiler (${BUILD_CONFIG})..."
dotnet build -q "$COMPILER_SLN" -c "$BUILD_CONFIG"

# Locate the driver DLL.
DRIVER_DLL="artifacts/compiler/z42.Driver/bin/${BUILD_CONFIG}/net10.0/z42.Driver.dll"
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

for dir in "$GOLDEN_DIR"/*/; do
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
