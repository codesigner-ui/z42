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
#   ./scripts/test-all.sh                     # all 6 required stages (default)
#   ./scripts/test-all.sh --scope=runtime     # skip dotnet build + dotnet test
#   ./scripts/test-all.sh --scope=compiler    # skip cargo build (runtime cached)
#   ./scripts/test-all.sh --scope=stdlib      # skip both builds + dotnet test
#   ./scripts/test-all.sh --scope=auto        # detect from `git diff --name-only HEAD`
#   ./scripts/test-all.sh --scope=full        # same as default
#   ./scripts/test-all.sh --with-dist         # also run packaged binary check
#   ./scripts/test-all.sh --quick             # skip rebuild inside test-vm.sh
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
for arg in "$@"; do
    case "$arg" in
        --with-dist)  WITH_DIST=true ;;
        --quick)      QUICK=true ;;
        --scope=*)    SCOPE="${arg#--scope=}" ;;
        -h|--help)
            sed -n '2,/^set -euo/p' "$0" | sed 's/^# \{0,1\}//;s/^#$//;/^set -euo/d'
            exit 0 ;;
        *) echo "unknown arg: $arg (try --help)" >&2; exit 2 ;;
    esac
done

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
STAGE_VM_GOLDENS="VM goldens|./scripts/test-vm.sh $($QUICK && echo '--no-rebuild' || true)"
STAGE_CROSS_ZPKG="cross-zpkg|./scripts/test-cross-zpkg.sh"
STAGE_STDLIB="stdlib [Test]|./scripts/test-stdlib.sh"
STAGE_DIST="packaged binary|./scripts/test-dist.sh"

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
        # Compiler unchanged → skip dotnet build + dotnet test.
        STAGES=(
            "$STAGE_CARGO_BUILD"
            "$STAGE_VM_GOLDENS"
            "$STAGE_CROSS_ZPKG"
            "$STAGE_STDLIB"
        )
        ;;
    compiler)
        # Runtime cached → skip cargo build. Compiler change may affect
        # emitted .zbc so VM / stdlib stages still run.
        STAGES=(
            "$STAGE_DOTNET_BUILD"
            "$STAGE_DOTNET_TEST"
            "$STAGE_VM_GOLDENS"
            "$STAGE_CROSS_ZPKG"
            "$STAGE_STDLIB"
        )
        ;;
    stdlib)
        # Both toolchains cached → skip builds + dotnet test.
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
