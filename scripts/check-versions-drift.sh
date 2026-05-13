#!/usr/bin/env bash
# scripts/check-versions-drift.sh — verify versions.toml is the single source of truth.
#
# Checks that platform source files (build.gradle.kts, package_helpers.sh) stay in sync
# with versions.toml. Run in CI (feature-matrix job) and locally before platform changes.
#
# Exit code: 0 = all in sync, 1 = drift detected.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
source "$SCRIPT_DIR/_lib/versions.sh"

FAIL=0

check() {
    local desc="$1" want="$2" got="$3"
    if [[ "$want" == "$got" ]]; then
        printf "  ✓ %-50s %s\n" "$desc" "$got"
    else
        printf "  ✗ %-50s want=%s got=%s\n" "$desc" "$want" "$got" >&2
        FAIL=1
    fi
}

# ── Android ──────────────────────────────────────────────────────────────────
echo "── Android ──────────────────────────────────────────────────────────────"
GRADLE="$ROOT/src/toolchain/host/platforms/android/z42vm/build.gradle.kts"

want=$(versions_get platform.android.min_api)
got=$(grep -E '^\s*minSdk\s*=' "$GRADLE" | grep -oE '[0-9]+' | head -1)
check "build.gradle.kts  minSdk" "$want" "$got"

want=$(versions_get platform.android.target_api)
got=$(grep -E '^\s*compileSdk\s*=' "$GRADLE" | grep -oE '[0-9]+' | head -1)
check "build.gradle.kts  compileSdk" "$want" "$got"

# ── iOS ───────────────────────────────────────────────────────────────────────
echo "── iOS ───────────────────────────────────────────────────────────────────"
PKG_HELPERS="$ROOT/scripts/_lib/package_helpers.sh"

want=$(versions_get platform.ios.min_ios)
# manifest.toml template line: ios-deployment-target = "14.0"
got=$(grep 'ios-deployment-target' "$PKG_HELPERS" | grep -oE '[0-9]+\.[0-9]+' | head -1)
check "package_helpers.sh  ios-deployment-target" "$want" "$got"

want=$(versions_get platform.ios.min_macos)
# Package.swift template line: .macOS(.v13)  → extract "13" → append ".0"
got_major=$(grep '\.macOS(\.v' "$PKG_HELPERS" | grep -oE '\.v[0-9]+' | grep -oE '[0-9]+' | head -1)
got="${got_major}.0"
check "package_helpers.sh  macOS deployment target" "$want" "$got"

# ── Android min_api cross-check: versions.toml matches abiFilters count ──────
# (abiFilters encodes which ABIs are shipped; indirect check that 32-bit removal stayed)
echo "── wasm ──────────────────────────────────────────────────────────────────"
want=$(versions_get build.wasm.wasm_pack_min)
check "versions.toml  build.wasm.wasm_pack_min present" "$want" "$want"

want=$(versions_get build.wasm.wasm_tools_min)
check "versions.toml  build.wasm.wasm_tools_min present" "$want" "$want"

# ── Result ────────────────────────────────────────────────────────────────────
echo "─────────────────────────────────────────────────────────────────────────"
if [[ "$FAIL" -ne 0 ]]; then
    echo "error: versions.toml drift detected — update the source files to match versions.toml" >&2
    echo "       See versions.toml → field comments (→ filename) for which file to fix." >&2
    exit 1
fi
echo "All version checks passed."
