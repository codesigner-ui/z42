#!/usr/bin/env bash
# scripts/package.sh — Build + assemble per-arch SDK package conforming
# to docs/spec/archive/2026-05-13-define-package-layout/.
#
# Dispatches on RID category:
#   - desktop  → bin/(z42c+z42vm) + native/(libz42.{a,dylib,so,dll}) + examples/(hello_c+hello_rust)
#   - ios      → bin/(README) + native/(libz42.a + Z42VM.xcframework) + Sources/ + Package.swift + examples/hello_c
#   - android  → bin/(README) + native/(libz42_platform_android.{a,so}) + kotlin/ + cpp/ + examples/hello_c
#   - wasm     → bin/(README) + native/(libz42.a + z42_wasm_bg.wasm) + pkg-web/ + pkg-nodejs/ + js/ + package.json + examples/hello_c
#
# Per memory project_supported_platforms — 11 RID whitelist:
#   desktop: macos-arm64 / linux-arm64 / linux-x64 / windows-x64
#   ios:     ios-arm64 / ios-arm64-sim
#   android: android-arm64 / android-armv7 / android-x64 / android-x86
#   wasm:    browser-wasm
#
# Usage:
#   ./scripts/package.sh release                       # host RID
#   ./scripts/package.sh release --rid ios-arm64       # explicit RID
#   ./scripts/package.sh --help

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

source "$SCRIPT_DIR/_lib/package_helpers.sh"

# ── Arg parsing ──────────────────────────────────────────────────────────

PROFILE="debug"
RID=""
VARIANT=""

usage() {
    cat <<EOF
Usage: $(basename "$0") [release|debug] [--rid <rid>] [--variant <suffix>]

Builds + assembles a z42 SDK distribution package.

Profile:  release | debug          (default: debug)

Options:
  --rid <rid>              Target RID (one of the 11 in whitelist):
                             desktop: macos-arm64 / linux-arm64 / linux-x64 / windows-x64
                             ios:     ios-arm64 / ios-arm64-sim
                             android: android-arm64 / android-armv7 / android-x64 / android-x86
                             wasm:    browser-wasm
                           (default: auto-detected host RID, desktop only)
  --variant <suffix>       Append "-<suffix>" to the package directory name.
  -h, --help               Show this help.

See memory project_supported_platforms for the supported-architectures
whitelist rationale.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        release|Release) PROFILE="release" ;;
        debug|Debug)     PROFILE="debug" ;;
        --rid)           RID="$2"; shift ;;
        --variant)       VARIANT="$2"; shift ;;
        -h|--help)       usage; exit 0 ;;
        *) echo "unknown arg: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

HOST_RID="$(detect_host_rid)"
RID="${RID:-$HOST_RID}"
validate_rid_supported_on_host "$RID"

CATEGORY=$(rid_category "$RID")
CARGO_TARGET=$(rid_to_cargo "$RID")

VERSION=$(grep -E '^version' src/runtime/Cargo.toml | head -1 | sed -E 's/.*"([^"]+)".*/\1/')
[ -z "$VERSION" ] && VERSION="0.0.0"

PKG_NAME="z42-${VERSION}-${RID}-${PROFILE}"
[ -n "$VARIANT" ] && PKG_NAME="${PKG_NAME}-${VARIANT}"
PKG_DIR="$ROOT/artifacts/packages/$PKG_NAME"

echo "Package:        $PKG_NAME"
echo "Output:         $PKG_DIR"
echo "Target RID:     $RID  (category=$CATEGORY, cargo=$CARGO_TARGET)"
echo "Profile:        $PROFILE"
echo "Host RID:       $HOST_RID"
echo ""

rm -rf "$PKG_DIR"
mkdir -p "$PKG_DIR/bin" "$PKG_DIR/libs" "$PKG_DIR/native/include" "$PKG_DIR/examples"

# ── Dispatch by category ─────────────────────────────────────────────────

case "$CATEGORY" in
    desktop) "$SCRIPT_DIR/_lib/package_desktop.sh" "$PKG_DIR" "$RID" "$VERSION" "$PROFILE" "$HOST_RID" ;;
    ios)     "$SCRIPT_DIR/_lib/package_ios.sh"     "$PKG_DIR" "$RID" "$VERSION" "$PROFILE" "$HOST_RID" ;;
    android) "$SCRIPT_DIR/_lib/package_android.sh" "$PKG_DIR" "$RID" "$VERSION" "$PROFILE" "$HOST_RID" ;;
    wasm)    "$SCRIPT_DIR/_lib/package_wasm.sh"    "$PKG_DIR" "$RID" "$VERSION" "$PROFILE" "$HOST_RID" ;;
    *) echo "error: unknown rid category '$CATEGORY' for rid '$RID'" >&2; exit 1 ;;
esac

# ── SHA-256 invariant check ──────────────────────────────────────────────

echo ""
echo "SHA-256 invariant check:"
pkg_sha256_check "$PKG_DIR" || exit 1

echo ""
echo "✅ Package assembled: $PKG_DIR/"
