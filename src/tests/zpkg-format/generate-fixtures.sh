#!/usr/bin/env bash
# src/tests/zpkg-format/generate-fixtures.sh — Regenerate all zpkg-format
# golden fixtures via the C# test harness's regen mode.
#
# Run after a legitimate zpkg wire format change (writer minor bump).
# CI does NOT call this; CI runs the same harness in assert mode and diffs.
#
# Spec: docs/spec/archive/<date>-freeze-zpkg-v0/

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

echo "── Regenerating zpkg-format fixtures via test harness ────────────────"
cd "$ROOT"
Z42_ZPKG_REGEN=1 dotnet test src/compiler/z42.Tests/z42.Tests.csproj \
    -c Release \
    --filter "FullyQualifiedName~Z42.Tests.Zpkg.FormatGoldenTests" \
    --logger "console;verbosity=minimal"

echo ""
echo "✓ Fixtures regenerated. Review git diff src/tests/zpkg-format/ + commit."
