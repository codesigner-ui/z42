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
#   6. test-lib.sh             — stdlib [Test] dogfood (all libs)
#
# Optional stages (skipped unless explicitly requested):
#   7. test-dist.sh            — packaged binary e2e (--with-dist; requires
#                                ./scripts/package.sh release run beforehand)
#
# Usage:
#   ./scripts/test-all.sh                     # all 6 required stages (default)
#   ./scripts/test-all.sh --scope=runtime     # skip dotnet build + dotnet test
#   ./scripts/test-all.sh --scope=compiler    # skip cargo build (runtime cached)
#   ./scripts/test-all.sh --scope=stdlib      # skip both builds + dotnet test
#   ./scripts/test-all.sh --scope=auto        # detect from `git diff --name-only HEAD`
#   ./scripts/test-all.sh --scope=full        # same as default
#   ./scripts/test-all.sh --parallel          # run independent stages in parallel waves
#   ./scripts/test-all.sh --with-dist         # also run packaged binary check
#   ./scripts/test-all.sh --quick             # skip rebuild inside test-vm.sh
#   ./scripts/test-all.sh --jobs 4            # run N tests in parallel within each stage
#   ./scripts/test-all.sh --jobs auto         # use all logical CPUs (nproc / sysctl)
#   ./scripts/test-all.sh --parallel --jobs 4 # stage-level + intra-stage parallelism
#
# Scope semantics (add-test-split-by-area, 2026-05-21):
#   - full      — all 6 stages (default). Use before commit / for PR gate.
#   - runtime   — cargo build + test-vm + cross-zpkg + stdlib. Skip dotnet
#                 stages. Use when iterating on src/runtime/** only.
#   - compiler  — dotnet build + dotnet test + test-vm + cross-zpkg + stdlib.
#                 Skip cargo build. Use when iterating on src/compiler/** only.
#   - stdlib    — test-vm + cross-zpkg + stdlib. Skip both builds + dotnet test.
#                 Use for pure src/libraries/** edits.
#   - docs-only — 0 stages (clean exit). Use for docs/.claude/spec-only edits.
#   - auto      — `git diff --name-only HEAD` → narrowest scope. Mixed
#                 changes resolve to `full`; only docs/.claude → `docs-only`.
#
# Parallel waves (add-test-parallel-stages, 2026-05-21):
# When `--parallel` is set, stages within each scope are grouped into 3
# dependency-respecting waves and the stages in each wave run concurrently:
#   W1: dotnet build || cargo build         (independent toolchains)
#   W2: dotnet test || test-stdlib          (independent inputs)
#   W3: test-vm --no-rebuild || cross-zpkg  (both consume W2's stdlib)
# Wave failure short-circuits subsequent waves. Stage outputs are captured
# and printed serially in their original order after each wave so the
# transcript remains readable (no interleaving). On failure, temp output
# files are preserved + their paths printed for debugging. `--parallel`
# implies `--no-rebuild` on test-vm to avoid racing W2's stdlib build.
#
# **GREEN gate rule**: iteration may use narrowed scope for speed. The
# final pre-commit GREEN MUST be `--scope=full` (or `--scope=auto` not
# narrower than what the commit touches). See workflow.md 阶段 8.
#
# Exit code: 0 if every selected stage passes, 1 otherwise (with a one-line
# failure summary). Pass-through stdout from each stage stays visible so
# CI logs read the same as running each script individually.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

SCOPE="full"
WITH_DIST=false
QUICK=false
PARALLEL=false
JOBS=1
for arg in "$@"; do
    case "$arg" in
        --with-dist)  WITH_DIST=true ;;
        --quick)      QUICK=true ;;
        --parallel)   PARALLEL=true ;;
        --scope=*)    SCOPE="${arg#--scope=}" ;;
        --jobs=*)     JOBS="${arg#--jobs=}" ;;
        --jobs)       echo "error: --jobs requires a value (e.g. --jobs=4 or --jobs=auto)" >&2; exit 2 ;;
        -h|--help)
            sed -n '2,/^set -euo/p' "$0" | sed 's/^# \{0,1\}//;s/^#$//;/^set -euo/d'
            exit 0 ;;
        *) echo "unknown arg: $arg (try --help)" >&2; exit 2 ;;
    esac
done

# Resolve --jobs auto → logical CPU count (cross-platform).
if [[ "$JOBS" == "auto" ]]; then
    JOBS=$(nproc 2>/dev/null || sysctl -n hw.logicalcpu 2>/dev/null || echo 4)
fi

# add-test-split-by-area (2026-05-21): auto-detect scope from uncommitted
# git changes. Path classification follows Decision 4 in design.md.
resolve_scope_from_diff() {
    local files
    files=$(git diff --name-only HEAD 2>/dev/null || true)
    if [ -z "$files" ]; then
        # No uncommitted changes — be safe, do full.
        echo "full"
        return
    fi

    local has_compiler=false has_runtime=false has_stdlib=false has_other=false
    while IFS= read -r f; do
        [ -z "$f" ] && continue
        case "$f" in
            src/compiler/*)             has_compiler=true ;;
            src/runtime/*|src/tests/*)  has_runtime=true ;;
            src/libraries/*|examples/*) has_stdlib=true ;;
            docs/*|.claude/*)           ;; # docs-only, doesn't elevate scope
            *)                          has_other=true ;;
        esac
    done <<< "$files"

    if $has_other; then echo "full"; return; fi
    if $has_compiler && $has_runtime; then echo "full"; return; fi
    if $has_compiler; then echo "compiler"; return; fi
    if $has_runtime; then echo "runtime"; return; fi
    if $has_stdlib; then echo "stdlib"; return; fi
    # Only docs / .claude touched.
    echo "docs-only"
}

if [ "$SCOPE" = "auto" ]; then
    SCOPE=$(resolve_scope_from_diff)
    echo "auto-detected scope: $SCOPE"
fi

# Define stage commands once; each scope picks a subset.
STAGE_DOTNET_BUILD="dotnet build|dotnet build src/compiler/z42.slnx --nologo -v quiet"
STAGE_CARGO_BUILD="cargo build (release)|cargo build --manifest-path src/runtime/Cargo.toml --release --quiet"
STAGE_DOTNET_TEST="dotnet test|dotnet test src/compiler/z42.Tests/z42.Tests.csproj --nologo"
_VM_QUICK_FLAG=$($QUICK && echo '--no-rebuild' || true)
STAGE_VM_GOLDENS="VM goldens|./scripts/test-vm.sh ${_VM_QUICK_FLAG} --jobs=${JOBS}"
# add-test-parallel-stages (2026-05-21): in parallel mode, force --no-rebuild
# on test-vm to avoid racing W2's stdlib build path.
STAGE_VM_GOLDENS_NOREBUILD="VM goldens|./scripts/test-vm.sh --no-rebuild --jobs=${JOBS}"
STAGE_CROSS_ZPKG="cross-zpkg|./scripts/test-cross-zpkg.sh"
STAGE_STDLIB="stdlib [Test]|./scripts/test-lib.sh --jobs=${JOBS}"
STAGE_DIST="packaged binary|./scripts/test-dist.sh"

# ── Sequential mode (existing behavior) ──────────────────────────────────────

run_stage_sequential() {
    local entry="$1"
    local name cmd
    IFS='|' read -r name cmd <<< "$entry"
    echo ""
    echo "════════════════════════════════════════════════"
    echo "  $name"
    echo "════════════════════════════════════════════════"
    bash -c "$cmd"
}

# ── Parallel mode (add-test-parallel-stages, 2026-05-21) ─────────────────────
#
# run_wave: launch each arg (a "name|cmd" stage entry) concurrently, capture
# stdout+stderr to temp files, wait for all, then print outputs serially in
# original stage order. Returns 0 if all stages pass, 1 if any fail. On
# failure, temp files are preserved and their paths are echoed so the user
# can inspect partial output.
run_wave() {
    local pids=() outs=() names=()
    for entry in "$@"; do
        local name cmd
        IFS='|' read -r name cmd <<< "$entry"
        local out
        out=$(mktemp -t z42-test-all.XXXXXX)
        names+=("$name")
        outs+=("$out")
        bash -c "$cmd" > "$out" 2>&1 &
        pids+=($!)
    done

    local fail=0
    local fail_idx=-1
    for i in "${!pids[@]}"; do
        if ! wait "${pids[$i]}"; then
            fail=1
            if [ $fail_idx -eq -1 ]; then fail_idx=$i; fi
        fi
    done

    # Print outputs in original stage order so the transcript is readable.
    for i in "${!outs[@]}"; do
        echo ""
        echo "════════════════════════════════════════════════"
        echo "  ${names[$i]}"
        echo "════════════════════════════════════════════════"
        cat "${outs[$i]}"
    done

    if [ $fail -eq 0 ]; then
        for o in "${outs[@]}"; do rm -f "$o"; done
        return 0
    fi
    echo ""
    echo "wave failed at stage: ${names[$fail_idx]}"
    echo "stage outputs preserved at:"
    for i in "${!outs[@]}"; do
        echo "  ${names[$i]}: ${outs[$i]}"
    done
    return 1
}

# ── Dispatch ─────────────────────────────────────────────────────────────────

if $PARALLEL; then
    total_stages=0
    total_waves=0
    # Sequential regen step run between W2 and W3 in every scope that includes
    # VM goldens. STAGE_VM_GOLDENS_NOREBUILD passes --no-rebuild to test-vm.sh,
    # which historically skipped BOTH stdlib rebuild AND golden zbc regen.
    # On a fresh CI checkout that produces an empty CASES array → silent
    # 0-tests-ran on bash 5+ and a hard "CASES[@]: unbound variable" on macOS
    # bash 3.2 (Apple's frozen version). We invoke regen-golden-tests.sh
    # --no-stdlib here so the W3 goldens have real fixtures to run; --no-stdlib
    # reuses W1's stdlib so there's no duplicate work or W2 race.
    regen_step() {
        echo ""
        echo "════════════════════════════════════════════════"
        echo "  regen golden zbc fixtures (--no-stdlib)"
        echo "════════════════════════════════════════════════"
        ./scripts/regen-golden-tests.sh --no-stdlib
    }
    case "$SCOPE" in
        full)
            run_wave "$STAGE_DOTNET_BUILD" "$STAGE_CARGO_BUILD"        || exit 1
            run_wave "$STAGE_DOTNET_TEST" "$STAGE_STDLIB"              || exit 1
            regen_step                                                 || exit 1
            run_wave "$STAGE_VM_GOLDENS_NOREBUILD" "$STAGE_CROSS_ZPKG" || exit 1
            total_stages=7; total_waves=4 ;;
        runtime)
            run_wave "$STAGE_CARGO_BUILD"                              || exit 1
            run_wave "$STAGE_STDLIB"                                   || exit 1
            regen_step                                                 || exit 1
            run_wave "$STAGE_VM_GOLDENS_NOREBUILD" "$STAGE_CROSS_ZPKG" || exit 1
            total_stages=5; total_waves=4 ;;
        compiler)
            run_wave "$STAGE_DOTNET_BUILD"                             || exit 1
            run_wave "$STAGE_DOTNET_TEST" "$STAGE_STDLIB"              || exit 1
            regen_step                                                 || exit 1
            run_wave "$STAGE_VM_GOLDENS_NOREBUILD" "$STAGE_CROSS_ZPKG" || exit 1
            total_stages=6; total_waves=4 ;;
        stdlib)
            run_wave "$STAGE_STDLIB"                                   || exit 1
            regen_step                                                 || exit 1
            run_wave "$STAGE_VM_GOLDENS_NOREBUILD" "$STAGE_CROSS_ZPKG" || exit 1
            total_stages=4; total_waves=3 ;;
        docs-only)
            total_stages=0; total_waves=0 ;;
        *)
            echo "unknown scope: $SCOPE (try full|runtime|compiler|stdlib|auto|docs-only)" >&2
            exit 2 ;;
    esac

    if [ "$WITH_DIST" = true ] && [ $total_stages -gt 0 ]; then
        run_wave "$STAGE_DIST" || exit 1
        total_stages=$((total_stages + 1))
        total_waves=$((total_waves + 1))
    fi

    echo ""
    echo "════════════════════════════════════════════════"
    if [ $total_stages -eq 0 ]; then
        echo "  ✅ ALL GREEN (0 stages — docs-only)"
    else
        echo "  ✅ ALL GREEN ($total_waves waves, $total_stages stages, scope=$SCOPE, parallel)"
    fi
    echo "════════════════════════════════════════════════"
    exit 0
fi

# ── Sequential path (default; unchanged behavior) ────────────────────────────

case "$SCOPE" in
    full)
        STAGES=(
            "$STAGE_DOTNET_BUILD"
            "$STAGE_CARGO_BUILD"
            "$STAGE_DOTNET_TEST"
            "$STAGE_VM_GOLDENS"
            "$STAGE_CROSS_ZPKG"
            "$STAGE_STDLIB"
        )
        ;;
    runtime)
        STAGES=(
            "$STAGE_CARGO_BUILD"
            "$STAGE_VM_GOLDENS"
            "$STAGE_CROSS_ZPKG"
            "$STAGE_STDLIB"
        )
        ;;
    compiler)
        STAGES=(
            "$STAGE_DOTNET_BUILD"
            "$STAGE_DOTNET_TEST"
            "$STAGE_VM_GOLDENS"
            "$STAGE_CROSS_ZPKG"
            "$STAGE_STDLIB"
        )
        ;;
    stdlib)
        STAGES=(
            "$STAGE_VM_GOLDENS"
            "$STAGE_CROSS_ZPKG"
            "$STAGE_STDLIB"
        )
        ;;
    docs-only)
        STAGES=()
        ;;
    *)
        echo "unknown scope: $SCOPE (try full|runtime|compiler|stdlib|auto|docs-only)" >&2
        exit 2 ;;
esac

if [ "$WITH_DIST" = true ]; then
    STAGES+=("$STAGE_DIST")
fi

# docs-only exits clean with 0 stages.
if [ ${#STAGES[@]} -eq 0 ]; then
    echo ""
    echo "════════════════════════════════════════════════"
    echo "  ✅ ALL GREEN (0 stages — docs-only)"
    echo "════════════════════════════════════════════════"
    exit 0
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
echo "  ✅ ALL GREEN (${#STAGES[@]} stages, scope=$SCOPE)"
echo "════════════════════════════════════════════════"
