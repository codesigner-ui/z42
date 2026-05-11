#!/usr/bin/env bash
# Build `Z42VM.xcframework` end-to-end:
#   1. Verify tooling (xcodebuild + 3 iOS rust targets).
#   2. Copy stdlib zpkgs from artifacts/z42/libs/ into Resources/stdlib/.
#   3. cargo build × 3 iOS targets in release.
#   4. lipo -create the two simulator slices into one universal.
#   5. xcodebuild -create-xcframework: device + universal-sim → Z42VM.xcframework.
#
# Spec: docs/spec/archive/2026-05-12-add-platform-ios/

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../../../.." && pwd)"
RUST_MANIFEST="$HERE/rust/Cargo.toml"
RUST_TARGET="$ROOT/artifacts/build/runtime"   # cargo target-dir override
LIB_NAME="libz42_platform_ios.a"

# ── (1) Tooling check (fail-fast). ───────────────────────────────────────

command -v xcodebuild >/dev/null 2>&1 || {
    echo "error: xcodebuild not found. Install Xcode + 'xcode-select --install'." >&2
    exit 1
}
command -v cargo >/dev/null 2>&1 || {
    echo "error: cargo not found. Install via https://rustup.rs" >&2
    exit 1
}
for t in aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios; do
    if ! rustup target list --installed | grep -q "^$t$"; then
        echo "error: rustc target $t not installed." >&2
        echo "       install via: rustup target add $t" >&2
        exit 1
    fi
done

# ── (2) Stdlib bundle. ───────────────────────────────────────────────────

LIBS_DIR="$ROOT/artifacts/z42/libs"
STDLIB_DIR="$HERE/Resources/stdlib"

if [[ -d "$LIBS_DIR" ]]; then
    echo "copying stdlib zpkgs from $LIBS_DIR"
    mkdir -p "$STDLIB_DIR"
    cp "$LIBS_DIR"/*.zpkg "$STDLIB_DIR/" 2>/dev/null || true
    ls "$STDLIB_DIR"/*.zpkg 2>/dev/null | xargs -n1 basename | sed 's/^/  - /' || true
else
    echo "warning: stdlib libs dir not found at $LIBS_DIR" >&2
    echo "         build the standard library first: dotnet build src/compiler/z42.slnx" >&2
fi

# ── (3) Cargo build × 3 iOS targets. ─────────────────────────────────────

for t in aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios; do
    echo "cargo build --release --target $t"
    cargo build --release --manifest-path "$RUST_MANIFEST" --target "$t"
done

DEV_LIB="$RUST_TARGET/aarch64-apple-ios/release/$LIB_NAME"
SIM_ARM64_LIB="$RUST_TARGET/aarch64-apple-ios-sim/release/$LIB_NAME"
SIM_X86_LIB="$RUST_TARGET/x86_64-apple-ios/release/$LIB_NAME"

for f in "$DEV_LIB" "$SIM_ARM64_LIB" "$SIM_X86_LIB"; do
    [[ -f "$f" ]] || { echo "error: expected lib not found: $f" >&2; exit 1; }
done

# ── (4) lipo simulator slices into one universal. ────────────────────────

SIM_UNIVERSAL="$HERE/build/sim-universal/$LIB_NAME"
mkdir -p "$(dirname "$SIM_UNIVERSAL")"
echo "lipo -create simulator slices"
lipo -create "$SIM_ARM64_LIB" "$SIM_X86_LIB" -output "$SIM_UNIVERSAL"
lipo -info "$SIM_UNIVERSAL"

# ── (5) xcodebuild -create-xcframework. ──────────────────────────────────

XCF="$HERE/Z42VM.xcframework"
rm -rf "$XCF"
echo "xcodebuild -create-xcframework -> $XCF"
xcodebuild -create-xcframework \
    -library "$DEV_LIB" \
    -library "$SIM_UNIVERSAL" \
    -output "$XCF"

echo ""
echo "built:"
echo "  $XCF/ios-arm64/"
echo "  $XCF/ios-arm64_x86_64-simulator/"
echo "  $STDLIB_DIR/"
echo ""
echo "consume from Xcode:  .package(path: \"$HERE\")"
