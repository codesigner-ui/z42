#!/usr/bin/env bash
# scripts/test-vm.sh — Run VM golden tests in interp and/or JIT mode.
#
# Usage:
#   ./scripts/test-vm.sh              # run both interp and jit
#   ./scripts/test-vm.sh interp       # interp only
#   ./scripts/test-vm.sh jit          # jit only
#
# Exit code: 0 if all selected tests pass, 1 otherwise.

set -euo pipefail

RUNTIME_MANIFEST="src/runtime/Cargo.toml"
GOLDEN_DIR="src/runtime/tests/golden/run"
MODES=("interp" "jit")

if [ $# -ge 1 ]; then
    MODES=("$1")
fi

# Build once before running tests
echo "Building VM..."
cargo build -q --manifest-path "$RUNTIME_MANIFEST"
echo ""

OVERALL_PASS=0
OVERALL_FAIL=0

for MODE in "${MODES[@]}"; do
    echo "══════════════════════════════════════"
    echo " Mode: $MODE"
    echo "══════════════════════════════════════"

    PASS=0
    FAIL=0
    FAILURES=()

    for dir in "$GOLDEN_DIR"/*/; do
        name=$(basename "$dir")
        expected="$dir/expected_output.txt"

        # Artifact priority: source.z42ir.json (legacy) > source.zbc (new format)
        artifact=""
        if [ -f "$dir/source.z42ir.json" ]; then
            artifact="$dir/source.z42ir.json"
        elif [ -f "$dir/source.zbc" ]; then
            artifact="$dir/source.zbc"
        fi

        [ -n "$artifact" ] && [ -f "$expected" ] || continue

        actual=$(cargo run -q --manifest-path "$RUNTIME_MANIFEST" -- "$artifact" --mode "$MODE" 2>&1) || true

        if [ "$actual" = "$(cat "$expected")" ]; then
            PASS=$((PASS + 1))
        else
            FAIL=$((FAIL + 1))
            FAILURES+=("$name")
            echo "  FAIL: $name"
            echo "    expected: $(head -1 "$expected")"
            echo "    actual:   $(echo "$actual" | head -1)"
        fi
    done

    echo ""
    echo "  Result: $PASS passed, $FAIL failed"
    echo ""

    OVERALL_PASS=$((OVERALL_PASS + PASS))
    OVERALL_FAIL=$((OVERALL_FAIL + FAIL))
done

echo "══════════════════════════════════════"
echo " Total: $OVERALL_PASS passed, $OVERALL_FAIL failed"
echo "══════════════════════════════════════"

[ "$OVERALL_FAIL" -eq 0 ]
