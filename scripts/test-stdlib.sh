#!/usr/bin/env bash
# Run stdlib library [Test] tests via z42-test-runner.
#
# Usage:
#   ./scripts/test-stdlib.sh              # all stdlib libs
#   ./scripts/test-stdlib.sh z42.math     # specific lib
#
# For each `src/libraries/<lib>/tests/*.z42`:
#   1. Compile to a .zbc via z42c (Release mode, single-file)
#   2. Run via z42-test-runner (which subprocesses to z42vm per [Test] entry)
#   3. Aggregate per-file pass/fail
#
# Exit codes:
#   0  all test files passed
#   1  one or more test files failed
#   2  build / tooling error

set -euo pipefail

LIB_FILTER="${1:-}"
ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"

echo "→ Preparing tooling (stdlib + z42vm + z42-test-runner; release)..."
./scripts/build-stdlib.sh >/dev/null
cargo build --manifest-path src/runtime/Cargo.toml --release --quiet
cargo build --manifest-path src/toolchain/test-runner/Cargo.toml --release --quiet

RUNNER="$ROOT/artifacts/rust/release/z42-test-runner"
if [[ ! -x "$RUNNER" ]]; then
    echo "error: z42-test-runner not built at $RUNNER" >&2
    exit 2
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

total_failed=0
total_files=0
total_libs_tested=0

for lib_dir in "$ROOT"/src/libraries/*/; do
    lib=$(basename "$lib_dir")

    # Apply --lib filter if given
    if [[ -n "$LIB_FILTER" && "$LIB_FILTER" != "$lib" ]]; then
        continue
    fi

    tests_dir="$lib_dir/tests"
    [[ -d "$tests_dir" ]] || continue

    # Count actual .z42 test files (skip .gitkeep, README.md, etc.)
    shopt -s nullglob
    test_files=("$tests_dir"/*.z42)
    shopt -u nullglob
    [[ ${#test_files[@]} -gt 0 ]] || continue

    total_libs_tested=$((total_libs_tested + 1))
    echo ""
    echo "════════════════════════════════════════════════"
    echo "  $lib  (${#test_files[@]} test file(s))"
    echo "════════════════════════════════════════════════"

    for test_file in "${test_files[@]}"; do
        total_files=$((total_files + 1))
        name=$(basename "$test_file" .z42)
        zbc="$WORK_DIR/${lib}_${name}.zbc"

        # Compile
        if ! dotnet run --project src/compiler/z42.Driver -c Release -- \
                "$test_file" --emit zbc -o "$zbc" >/dev/null 2>&1; then
            echo ""
            echo "✗ COMPILE ERROR: $test_file"
            dotnet run --project src/compiler/z42.Driver -c Release -- \
                "$test_file" --emit zbc -o "$zbc" 2>&1 | tail -10
            total_failed=$((total_failed + 1))
            continue
        fi

        # Run
        if ! "$RUNNER" "$zbc"; then
            total_failed=$((total_failed + 1))
        fi
    done
done

echo ""
echo "════════════════════════════════════════════════"

if [[ -n "$LIB_FILTER" && "$total_libs_tested" -eq 0 ]]; then
    echo "  no library matched: $LIB_FILTER" >&2
    exit 2
fi

if [[ "$total_failed" -gt 0 ]]; then
    echo "  ❌ stdlib tests: $total_failed file(s) failed (of $total_files file(s) in $total_libs_tested lib(s))"
    exit 1
else
    echo "  ✅ stdlib tests: all $total_files file(s) passed (in $total_libs_tested lib(s))"
    exit 0
fi
