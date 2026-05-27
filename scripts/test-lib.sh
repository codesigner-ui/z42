#!/usr/bin/env bash
# Run [Test] tests for one or more z42 standard library packages.
#
# Usage:
#   ./scripts/test-lib.sh                           # all libs
#   ./scripts/test-lib.sh z42.io                    # single lib
#   ./scripts/test-lib.sh z42.io z42.net            # multiple libs
#   ./scripts/test-lib.sh --jobs 4                  # parallel (N concurrent test files)
#   ./scripts/test-lib.sh z42.io --jobs 4           # single lib, parallel
#   ./scripts/test-lib.sh z42.io --no-build         # skip stdlib/tooling rebuild
#   ./scripts/test-lib.sh z42.io -k process_basic   # filter by file name (substring)
#
# Each matching src/libraries/<lib>/tests/*.z42 is:
#   1. Compiled to .zbc via z42c (Release mode)
#   2. Run via z42-test-runner (subprocesses z42vm per [Test] method)
#   3. Aggregated into a pass/fail summary
#
# Parallel mode (--jobs N > 1):
#   Test files within each library are dispatched in batches of N. Each batch
#   captures output to temp files and prints in original order after the batch
#   completes, so output remains readable even at high parallelism.
#
# Exit codes:  0=all pass  1=some fail  2=tooling/argument error

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"

# ── Argument parsing ──────────────────────────────────────────────────────────

LIBS=()
JOBS=1
NO_BUILD=false
FILTER=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --jobs|-j)
            [[ $# -ge 2 ]] || { echo "error: --jobs requires a value" >&2; exit 2; }
            JOBS="$2"; shift 2 ;;
        --jobs=*)   JOBS="${1#--jobs=}"; shift ;;
        -j[0-9]*)   JOBS="${1#-j}"; shift ;;
        --no-build) NO_BUILD=true; shift ;;
        -k)
            [[ $# -ge 2 ]] || { echo "error: -k requires a pattern" >&2; exit 2; }
            FILTER="$2"; shift 2 ;;
        -k*)        FILTER="${1#-k}"; shift ;;
        -h|--help)
            sed -n '2,/^set -euo/p' "$0" | sed 's/^# \{0,1\}//;/^set -euo/d'
            exit 0 ;;
        -*) echo "error: unknown flag: $1 (try --help)" >&2; exit 2 ;;
        *)  LIBS+=("$1"); shift ;;
    esac
done

if ! [[ "$JOBS" =~ ^[0-9]+$ ]] || [[ "$JOBS" -lt 1 ]]; then
    echo "error: --jobs must be a positive integer, got: $JOBS" >&2
    exit 2
fi

# ── Early lib validation (before slow tooling build) ─────────────────────────

for lib in ${LIBS[@]+"${LIBS[@]}"}; do
    if [[ ! -d "$ROOT/src/libraries/$lib" ]]; then
        echo "error: library not found: src/libraries/$lib" >&2
        echo "       available: $(ls "$ROOT/src/libraries/" | tr '\n' ' ')" >&2
        exit 2
    fi
done

# ── Tooling ───────────────────────────────────────────────────────────────────

if ! $NO_BUILD; then
    echo "→ Preparing tooling (stdlib + z42vm + z42-test-runner; release)..."
    "$SCRIPT_DIR/build-stdlib.sh" >/dev/null
    cargo build --manifest-path src/runtime/Cargo.toml --release --quiet
    cargo build --manifest-path src/toolchain/test-runner/Cargo.toml --release --quiet
fi

RUNNER="$ROOT/artifacts/build/runtime/release/z42-test-runner"
if [[ ! -x "$RUNNER" ]]; then
    echo "error: z42-test-runner not found at $RUNNER" >&2
    echo "       run without --no-build, or build manually:" >&2
    echo "       cargo build --manifest-path src/toolchain/test-runner/Cargo.toml --release" >&2
    exit 2
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

# ── Library discovery ─────────────────────────────────────────────────────────

lib_dirs=()
if [[ ${#LIBS[@]} -eq 0 ]]; then
    for d in "$ROOT/src/libraries"/*/; do
        [[ -d "$d" ]] && lib_dirs+=("$d")
    done
else
    for lib in "${LIBS[@]}"; do
        lib_dirs+=("$ROOT/src/libraries/$lib/")
    done
fi

# ── Per-file compile+run (runs in a subshell for parallel dispatch) ───────────
#
# Usage: run_test_file <lib> <test_file> <zbc_path>
# Stdout: z42-test-runner output (pass/fail per [Test] method) plus any
#         compile error details. Exit code: 0=pass, 1=fail.

run_test_file() {
    local lib="$1" test_file="$2" zbc="$3"
    local name
    name=$(basename "$test_file" .z42)

    if ! dotnet run --project "$ROOT/src/compiler/z42.Driver" -c Release -- \
            "$test_file" --emit zbc -o "$zbc" >/dev/null 2>&1; then
        echo ""
        echo "✗ COMPILE ERROR: $name"
        dotnet run --project "$ROOT/src/compiler/z42.Driver" -c Release -- \
            "$test_file" --emit zbc -o "$zbc" 2>&1 | tail -10
        return 1
    fi

    # add-test-runner-parallel (2026-05-27): pass --jobs through to the
    # runner. JOBS == 1 is the default — runner runs serially via the
    # in-process path (Setup/Teardown preserved). JOBS > 1 switches the
    # runner to parallel subprocess execution.
    if [[ "$JOBS" -gt 1 ]]; then
        "$RUNNER" --jobs "$JOBS" "$zbc"
    else
        "$RUNNER" "$zbc"
    fi
}

# ── Main loop ─────────────────────────────────────────────────────────────────

total_failed=0
total_files=0
total_libs_tested=0

for lib_dir in "${lib_dirs[@]}"; do
    lib=$(basename "$lib_dir")
    tests_dir="${lib_dir}tests"
    [[ -d "$tests_dir" ]] || continue

    shopt -s nullglob
    all_test_files=("$tests_dir"/*.z42)
    shopt -u nullglob
    [[ ${#all_test_files[@]} -gt 0 ]] || continue

    # Apply -k filter
    test_files=()
    for f in "${all_test_files[@]}"; do
        name=$(basename "$f" .z42)
        if [[ -z "$FILTER" || "$name" == *"$FILTER"* ]]; then
            test_files+=("$f")
        fi
    done
    [[ ${#test_files[@]} -gt 0 ]] || continue

    total_libs_tested=$((total_libs_tested + 1))
    echo ""
    echo "════════════════════════════════════════════════"
    echo "  $lib  (${#test_files[@]} test file(s)${FILTER:+ matching '$FILTER'})"
    echo "════════════════════════════════════════════════"

    lib_failed=0

    if [[ "$JOBS" -le 1 ]]; then
        # ── Sequential: stream output directly ───────────────────────────────
        for test_file in "${test_files[@]}"; do
            total_files=$((total_files + 1))
            name=$(basename "$test_file" .z42)
            zbc="$WORK_DIR/${lib}_${name}.zbc"
            if ! run_test_file "$lib" "$test_file" "$zbc"; then
                lib_failed=$((lib_failed + 1))
            fi
        done
    else
        # ── Parallel: batch of JOBS files at a time ───────────────────────────
        # Each job captures output to a temp file. After the batch completes,
        # outputs are printed in original order so results stay readable.
        n_files=${#test_files[@]}
        i=0
        while [[ $i -lt $n_files ]]; do
            batch_pids=(); batch_outs=(); batch_rcs=(); batch_names=()

            for ((j = 0; j < JOBS && i + j < n_files; j++)); do
                test_file="${test_files[$((i + j))]}"
                name=$(basename "$test_file" .z42)
                zbc="$WORK_DIR/${lib}_${name}.zbc"
                out_f=$(mktemp "$WORK_DIR/out.XXXXXX")
                rc_f=$(mktemp "$WORK_DIR/rc.XXXXXX")
                echo "1" > "$rc_f"

                (
                    if run_test_file "$lib" "$test_file" "$zbc"; then
                        echo "0" > "$rc_f"
                    else
                        echo "1" > "$rc_f"
                    fi
                ) > "$out_f" 2>&1 &

                batch_pids+=($!)
                batch_outs+=("$out_f")
                batch_rcs+=("$rc_f")
                batch_names+=("$name")
            done

            # Wait for every job in this batch before moving on.
            for pid in "${batch_pids[@]}"; do
                wait "$pid" 2>/dev/null || true
            done

            # Print outputs in original order and aggregate.
            for k in "${!batch_names[@]}"; do
                total_files=$((total_files + 1))
                cat "${batch_outs[$k]}"
                rc=$(cat "${batch_rcs[$k]}" 2>/dev/null || echo "1")
                if [[ "$rc" != "0" ]]; then
                    lib_failed=$((lib_failed + 1))
                fi
            done

            i=$((i + JOBS))
        done
    fi

    total_failed=$((total_failed + lib_failed))
done

# ── Summary ───────────────────────────────────────────────────────────────────

echo ""
echo "════════════════════════════════════════════════"

if [[ ${#LIBS[@]} -gt 0 && "$total_libs_tested" -eq 0 ]]; then
    echo "  no tests found for: ${LIBS[*]}" >&2
    exit 2
fi

if [[ "$total_failed" -gt 0 ]]; then
    echo "  ❌ stdlib tests: $total_failed file(s) failed (of $total_files in $total_libs_tested lib(s))"
    exit 1
else
    echo "  ✅ stdlib tests: all $total_files file(s) passed (in $total_libs_tested lib(s))"
    exit 0
fi
