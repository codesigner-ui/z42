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

# redesign-artifact-layout (2026-05-12): 取 packages/ 内最新的 host-RID release 包
# 用户可显式覆盖：DIST_DIR=artifacts/packages/z42-... ./scripts/test-dist.sh
DIST_DIR="${DIST_DIR:-}"
if [ -z "$DIST_DIR" ]; then
    DIST_DIR=$(ls -td "$ROOT"/artifacts/packages/z42-*-release 2>/dev/null | head -1)
fi
if [ -z "$DIST_DIR" ] || [ ! -d "$DIST_DIR" ]; then
    echo "error: no packaged distribution found at artifacts/packages/z42-*-release"
    echo "       Run: ./scripts/package.sh release"
    exit 1
fi
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

# ── Launcher smoke (bundle-launcher-in-release) ──────────────────────────────
# The package ships bin/z42 (trampoline) + launcher.zpkg. Verify portable
# resolution works: with no $Z42_HOME, `z42 which` runs the launcher core via
# the package-relative z42vm and reports that same bundled z42vm.
Z42_LAUNCHER_BIN="$DIST_DIR/bin/z42"
[ -f "$DIST_DIR/bin/z42.exe" ] && Z42_LAUNCHER_BIN="$DIST_DIR/bin/z42.exe"
if [ -x "$Z42_LAUNCHER_BIN" ] && [ -f "$DIST_DIR/launcher.zpkg" ]; then
    echo "Launcher smoke (portable): $Z42_LAUNCHER_BIN"
    which_out=$(env -u Z42_HOME "$Z42_LAUNCHER_BIN" which 2>&1 || true)
    case "$which_out" in
        "$DIST_DIR/bin/z42vm"|"$DIST_DIR/bin/z42vm.exe")
            echo "  ✓ z42 which → bundled z42vm"; OVERALL_PASS=$((OVERALL_PASS + 1)) ;;
        *)
            echo "  FAIL: z42 which → '$which_out' (expected $DIST_DIR/bin/z42vm)"
            OVERALL_FAIL=$((OVERALL_FAIL + 1)) ;;
    esac
    echo ""
else
    echo "Launcher smoke: SKIP (bin/z42 or launcher.zpkg not in package)"
    echo ""
fi

for MODE in "${MODES[@]}"; do
    echo "══════════════════════════════════════"
    echo " Mode: $MODE"
    echo "══════════════════════════════════════"

    PASS=0
    FAIL=0
    COMPILE_FAIL=0
    FAILURES=()

    # Enumerate cases in dual layout (dir + flat). Tuples: "name|source|expected|interp_only_marker".
    CASES=()
    for glob in "${GOLDEN_GLOBS[@]}"; do
        for d in $glob; do
            [ -d "$d" ] || continue
            case "$d" in
                */src/tests/cross-zpkg/*) continue ;;
            esac
            [ -f "$d/source.z42" ] || continue
            name=$(basename "$d")
            CASES+=("$name|$d/source.z42|$d/expected_output.txt|$d/interp_only")
        done
    done
    # Flat mode: only src/tests/ (libraries flat .z42 are test-runner cases).
    for f in "$ROOT/src/tests/"*"/"*".z42"; do
        [ -f "$f" ] || continue
        case "$f" in
            */src/tests/errors/*|*/src/tests/parse/*|*/src/tests/cross-zpkg/*) continue ;;
        esac
        name=$(basename "$f" .z42)
        dir=$(dirname "$f")
        CASES+=("$name|$f|$dir/$name.expected_output.txt|$dir/$name.interp_only")
    done

    for entry in "${CASES[@]}"; do
        IFS='|' read -r name source expected interp_marker <<< "$entry"

        # Skip JIT for tests with `interp_only` marker (mirrors test-vm.sh).
        if [ "$MODE" = "jit" ] && [ -f "$interp_marker" ]; then
            continue
        fi

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
