#!/usr/bin/env bash
# src/tests/zbc-format/generate-fixtures.sh — Regenerate all zbc-format golden fixtures.
#
# Run after a legitimate .zbc wire format change (writer minor bump). CI does
# NOT call this; CI invokes the in-process FormatGoldenTests harness which
# regenerates in-memory and diffs against checked-in bytes — diff = fail.
#
# Spec: docs/spec/archive/<date>-freeze-zbc-v1/

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

echo "── Building compiler driver (Release) ─────────────────────────────────"
dotnet build -q "$ROOT/src/compiler/z42.Driver/z42.Driver.csproj" -c Release

DRIVER_DLL="$ROOT/artifacts/build/compiler/z42.Driver/bin/z42c.dll"
[ -f "$DRIVER_DLL" ] || { echo "error: $DRIVER_DLL missing"; exit 1; }

echo "── Regenerating fixtures ───────────────────────────────────────────────"
PASS=0
FAIL=0
FAILURES=()

for case_dir in "$SCRIPT_DIR"/*/; do
    [ -f "$case_dir/source.z42" ] || continue
    name=$(basename "$case_dir")
    src="$case_dir/source.z42"
    zbc="$case_dir/source.zbc"
    json="$case_dir/expected.json"

    if dotnet "$DRIVER_DLL" "$src" --emit zbc -o "$zbc" >/dev/null 2>&1 \
       && dotnet "$DRIVER_DLL" golden-json "$zbc" -o "$json" >/dev/null 2>&1; then
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
echo " Regenerated: $PASS ok, $FAIL failed"
echo "══════════════════════════════════════"

if [ "${#FAILURES[@]}" -gt 0 ]; then
    echo ""
    echo "Failed fixtures:"
    for f in "${FAILURES[@]}"; do echo "  - $f"; done
    exit 1
fi
