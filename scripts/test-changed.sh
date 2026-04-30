#!/usr/bin/env bash
# scripts/test-changed.sh — Run only the test commands affected by changes
# under the working tree (vs a base ref).
#
# Maps each changed file to a coarse-grained test command, deduplicates the
# resulting set, and executes the union in a fixed order. See
# docs/design/testing.md "增量测试" for the full mapping table.
#
# Usage:
#   ./scripts/test-changed.sh                 # base = HEAD (unstaged + staged)
#   ./scripts/test-changed.sh main            # base = main (full branch delta)
#   ./scripts/test-changed.sh --dry-run       # print plan, don't execute
#   Z42_TEST_CHANGED_BASE=origin/main ./scripts/test-changed.sh
#
# Exit codes:
#   0  — all selected commands passed (or no commands to run)
#   N  — first failing command's exit code (passed through)
#   2  — tool error (not a git repo, git diff failed)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

# ── Argument / base resolution ────────────────────────────────────────────

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

if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    echo "[test-changed] error: not a git repository" >&2
    exit 2
fi

echo "[test-changed] base = $BASE"

# Collect changed files: tracked diff + untracked. We use --name-only against
# BASE for tracked changes (covers staged + unstaged), then ls-files
# --others --exclude-standard for new untracked files.
TRACKED=$(git diff --name-only "$BASE" -- 2>/dev/null || true)
UNTRACKED=$(git ls-files --others --exclude-standard 2>/dev/null || true)
CHANGED=$(printf "%s\n%s\n" "$TRACKED" "$UNTRACKED" | grep -v '^$' | sort -u || true)

if [[ -z "$CHANGED" ]]; then
    echo "[test-changed] no changed files; nothing to run"
    exit 0
fi

echo "[test-changed] changed files ($(echo "$CHANGED" | wc -l | tr -d ' ')):"
echo "$CHANGED" | sed 's/^/  /'

# ── Mapping rules ────────────────────────────────────────────────────────
#
# Output: lines added to PLAN, deduped via sort -u at the end. Each line is
# one shell command to execute. Order within a single file's mapping doesn't
# matter; the final EXECUTION order is fixed (compile → vm → stdlib → cross).

PLAN=""
add_plan() { PLAN="$PLAN
$1"; }

NEEDS_FULL=0
TOUCHED_ANY=0

while IFS= read -r f; do
    [[ -z "$f" ]] && continue
    case "$f" in
        # Documentation / spec / claude state — never trigger tests
        *.md|docs/*|spec/*|.claude/*|.gitignore|.gitattributes|LICENSE*|README*)
            ;;

        # Stdlib library: source vs tests vs manifest
        src/libraries/*/src/*)
            lib=$(echo "$f" | cut -d/ -f3)
            add_plan "just test-stdlib $lib"
            add_plan "just test-vm"
            TOUCHED_ANY=1 ;;
        src/libraries/*/tests/*)
            lib=$(echo "$f" | cut -d/ -f3)
            add_plan "just test-stdlib $lib"
            TOUCHED_ANY=1 ;;
        src/libraries/*/*.toml)
            lib=$(echo "$f" | cut -d/ -f3)
            add_plan "just test-stdlib $lib"
            TOUCHED_ANY=1 ;;
        src/libraries/*)
            # Catch-all (workspace.toml, README.md inside lib root etc.)
            ;;

        # Runtime (Rust VM)
        src/runtime/src/*|src/runtime/Cargo.toml|src/runtime/Cargo.lock|src/runtime/build.rs)
            add_plan "cargo test --manifest-path src/runtime/Cargo.toml"
            add_plan "just test-vm"
            TOUCHED_ANY=1 ;;
        src/runtime/tests/cross-zpkg/*)
            add_plan "just test-cross-zpkg"
            TOUCHED_ANY=1 ;;
        src/runtime/tests/*)
            add_plan "just test-vm"
            TOUCHED_ANY=1 ;;

        # C# compiler
        src/compiler/*)
            add_plan "just test-compiler"
            add_plan "just test-vm"
            TOUCHED_ANY=1 ;;

        # Toolchain (test-runner, others)
        src/toolchain/*)
            add_plan "cargo test --manifest-path src/toolchain/test-runner/Cargo.toml"
            add_plan "just test-stdlib"
            TOUCHED_ANY=1 ;;

        # Test infrastructure scripts
        scripts/test-vm.sh|scripts/regen-golden-tests.sh)
            add_plan "just test-vm"
            TOUCHED_ANY=1 ;;
        scripts/test-stdlib.sh|scripts/build-stdlib.sh)
            add_plan "just test-stdlib"
            TOUCHED_ANY=1 ;;
        scripts/test-cross-zpkg.sh)
            add_plan "just test-cross-zpkg"
            TOUCHED_ANY=1 ;;

        # justfile, top-level config — full sweep
        justfile|src/runtime/build.rs|*.workspace.toml)
            NEEDS_FULL=1
            TOUCHED_ANY=1 ;;

        # Anything else under src/ → full sweep (defensive)
        src/*)
            NEEDS_FULL=1
            TOUCHED_ANY=1 ;;

        # Examples / artifacts / unknown — no test impact
        examples/*|artifacts/*|bench/*)
            ;;
        *)
            # Unknown root-level file — be safe and run full
            NEEDS_FULL=1
            TOUCHED_ANY=1 ;;
    esac
done <<< "$CHANGED"

if [[ "$NEEDS_FULL" -eq 1 ]]; then
    PLAN="
just test"
fi

# Dedup, drop blanks
PLAN_UNIQ=$(echo "$PLAN" | grep -v '^$' | awk '!seen[$0]++' || true)

if [[ -z "$PLAN_UNIQ" ]]; then
    if [[ "$TOUCHED_ANY" -eq 0 ]]; then
        echo "[test-changed] only documentation / spec / config touched; no tests to run"
    else
        echo "[test-changed] no test commands mapped (changes don't affect any test scope)"
    fi
    exit 0
fi

# ── Plan output ──────────────────────────────────────────────────────────

echo "[test-changed] plan:"
echo "$PLAN_UNIQ" | sed 's/^/  → /'

if [[ "$DRY_RUN" -eq 1 ]]; then
    echo "[test-changed] --dry-run: not executing"
    exit 0
fi

# ── Execution ────────────────────────────────────────────────────────────

echo "[test-changed] running..."
echo ""

EXIT=0
while IFS= read -r cmd; do
    [[ -z "$cmd" ]] && continue
    echo "──── $cmd ────"
    if ! bash -c "$cmd"; then
        EXIT=$?
        echo "[test-changed] command failed: $cmd  (exit $EXIT)"
        break
    fi
    echo ""
done <<< "$PLAN_UNIQ"

if [[ "$EXIT" -eq 0 ]]; then
    echo "[test-changed] result: ok"
fi
exit "$EXIT"
