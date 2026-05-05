#!/usr/bin/env bash
# scripts/test-dist.sh — End-to-end tests using the packaged z42 distribution.
#
# Runs golden tests by compiling source.z42 with the packaged z42c compiler
# and executing the result with the packaged z42vm — no dotnet/cargo required.
#
# Prerequisites:
#   ./scripts/package.sh        # build z42c + z42vm
#   ./scripts/build-stdlib.sh   # compile stdlib packages
#
# Usage:
#   ./scripts/test-dist.sh              # run both interp and jit
#   ./scripts/test-dist.sh interp       # interp only
#   ./scripts/test-dist.sh jit          # jit only
#
# Exit code: 0 if all selected tests pass, 1 otherwise.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."

DIST_DIR="$ROOT/artifacts/z42"
Z42C="$DIST_DIR/bin/z42c"
Z42VM="$DIST_DIR/bin/z42vm"
LIBS_DIR="$DIST_DIR/libs"
GOLDEN_GLOBS=(
    "$ROOT/src/tests/"*"/"*"/"
    "$ROOT/src/libraries/"*"/tests/"*"/"
)
MODES=("interp" "jit")

if [ $# -ge 1 ]; then
    MODES=("$1")
fi

# ── Preflight checks ─────────────────────────────────────────────────────────
errors=0
if [ ! -x "$Z42C" ]; then
    echo "error: z42c not found at $Z42C"
    echo "       Run: ./scripts/package.sh"
    errors=1
fi
if [ ! -x "$Z42VM" ]; then
    echo "error: z42vm not found at $Z42VM"
    echo "       Run: ./scripts/package.sh"
    errors=1
fi
if [ ! -d "$LIBS_DIR" ]; then
    echo "error: libs directory not found at $LIBS_DIR"
    echo "       Run: ./scripts/build-stdlib.sh"
    errors=1
fi
if [ "$errors" -ne 0 ]; then
    exit 1
fi

echo "z42 Distribution Test Runner"
echo "  compiler: $Z42C"
echo "  vm:       $Z42VM"
echo "  libs:     $LIBS_DIR"
echo ""

# Temporary directory for compiled artifacts
TMPDIR_BASE=$(mktemp -d)
trap 'rm -rf "$TMPDIR_BASE"' EXIT

OVERALL_PASS=0
OVERALL_FAIL=0
OVERALL_COMPILE_FAIL=0

for MODE in "${MODES[@]}"; do
    echo "══════════════════════════════════════"
    echo " Mode: $MODE"
    echo "══════════════════════════════════════"

    PASS=0
    FAIL=0
    COMPILE_FAIL=0
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
        expected="$dir/expected_output.txt"

        # Skip non-run categories.
        case "$dir" in
            */src/tests/errors/*|*/src/tests/parse/*|*/src/tests/cross-zpkg/*) continue ;;
        esac
        [ -f "$source" ] || continue

        # Compile: source.z42 → temp.zbc using packaged z42c
        tmpout="$TMPDIR_BASE/${name}.zbc"
        if ! "$Z42C" "$source" --emit zbc -o "$tmpout" 2>/dev/null; then
            COMPILE_FAIL=$((COMPILE_FAIL + 1))
            FAILURES+=("$name (compile failed)")
            echo "  FAIL: $name (compile failed)"
            continue
        fi

        # Run: temp.zbc → z42vm. Empty expected_output.txt OK (Assert-based test).
        actual=$(Z42_LIBS="$LIBS_DIR" "$Z42VM" "$tmpout" --mode "$MODE" 2>&1) || true
        expected_str=""
        [ -f "$expected" ] && expected_str=$(cat "$expected")

        if [ "$actual" = "$expected_str" ]; then
            PASS=$((PASS + 1))
        else
            FAIL=$((FAIL + 1))
            FAILURES+=("$name")
            echo "  FAIL: $name"
            if [ -f "$expected" ]; then
                echo "    expected: $(head -1 "$expected")"
            else
                echo "    expected: <empty>"
            fi
            echo "    actual:   $(echo "$actual" | head -1)"
        fi
    done

    echo ""
    echo "  Result: $PASS passed, $FAIL failed, $COMPILE_FAIL compile errors"

    if [ "${#FAILURES[@]}" -gt 0 ]; then
        echo "  Failures:"
        for f in "${FAILURES[@]}"; do
            echo "    - $f"
        done
    fi

    echo ""

    OVERALL_PASS=$((OVERALL_PASS + PASS))
    OVERALL_FAIL=$((OVERALL_FAIL + FAIL))
    OVERALL_COMPILE_FAIL=$((OVERALL_COMPILE_FAIL + COMPILE_FAIL))
done

echo "══════════════════════════════════════"
echo " Total: $OVERALL_PASS passed, $OVERALL_FAIL failed, $OVERALL_COMPILE_FAIL compile errors"
echo "══════════════════════════════════════"

[ "$OVERALL_FAIL" -eq 0 ] && [ "$OVERALL_COMPILE_FAIL" -eq 0 ]
