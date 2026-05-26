#!/usr/bin/env bash
# Build `Z42VM.xcframework` end-to-end and stage test bundle resources:
#   1. Verify tooling (xcodebuild + 4 rust targets: 3 iOS + 1 macOS-arm64).
#   2. Copy stdlib zpkgs from artifacts/build/libs/release/ into Resources/stdlib/.
#   3. cargo build × 4 targets in release.
#   4. lipo -create the two iOS simulator slices into one universal.
#   5. xcodebuild -create-xcframework: ios-device + sim-universal + macos-arm64.
#   6. Compile test fixtures (examples/embedding/*.z42 → Tests/.../*.zbc).
#   7. Stage test bundle stdlib (Tests/.../Resources/stdlib).
#
# Spec: docs/spec/archive/2026-05-12-add-platform-ios/
#       docs/spec/changes/add-ios-tests/ (test target + macOS slice + fixtures)

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../../../.." && pwd)"
RUST_MANIFEST="$HERE/rust/Cargo.toml"
RUST_TARGET="$ROOT/artifacts/build/runtime"   # cargo target-dir override
LIB_NAME="libz42_platform_ios.a"

# 单一真相源：repo 根 versions.toml；helper 在 scripts/_lib/versions.sh.
source "$ROOT/scripts/_lib/versions.sh"
versions_require_python3
versions_check_rust          # 校 rustc 版本 vs [toolchain.rust]
versions_check_dotnet        # 校 dotnet SDK vs [toolchain.dotnet]

IOS_RUST_TARGETS=$(versions_get_list platform.ios.rust_targets)
HOST_EXTRA=$(versions_get_list build.ios.extra_rust_targets)
XCODE_MIN=$(versions_get build.ios.xcode_min)
ALL_TARGETS="$IOS_RUST_TARGETS $HOST_EXTRA"

# ── (1) Tooling check (fail-fast). ───────────────────────────────────────

command -v xcodebuild >/dev/null 2>&1 || {
    echo "error: xcodebuild not found (z42 requires Xcode $XCODE_MIN+; versions.toml [build.ios])." >&2
    echo "       install Xcode + 'xcode-select --install'." >&2
    exit 1
}
command -v cargo >/dev/null 2>&1 || {
    echo "error: cargo not found. Install via https://rustup.rs" >&2
    exit 1
}
command -v dotnet >/dev/null 2>&1 || {
    echo "error: dotnet not found. Install .NET 8+ (https://dotnet.microsoft.com)." >&2
    exit 1
}
for t in $ALL_TARGETS; do
    if ! rustup target list --installed | grep -q "^$t$"; then
        echo "error: rustc target $t not installed (declared in versions.toml [platform.ios]/[build.ios])." >&2
        echo "       install via: rustup target add $t" >&2
        exit 1
    fi
done

DRIVER_DLL="$ROOT/artifacts/build/compiler/z42.Driver/bin/z42c.dll"
if [[ ! -f "$DRIVER_DLL" ]]; then
    echo "error: z42c driver not found at $DRIVER_DLL" >&2
    echo "       build the compiler first: dotnet build $ROOT/src/compiler/z42.slnx" >&2
    exit 1
fi

# ── (2) Stdlib bundle. ───────────────────────────────────────────────────

LIBS_DIR="$ROOT/artifacts/build/libs/release"
STDLIB_DIR="$HERE/Resources/stdlib"

if [[ -d "$LIBS_DIR" ]]; then
    echo "copying stdlib zpkgs from $LIBS_DIR"
    mkdir -p "$STDLIB_DIR"
    cp "$LIBS_DIR"/*.zpkg "$STDLIB_DIR/" 2>/dev/null || true
    ls "$STDLIB_DIR"/*.zpkg 2>/dev/null | xargs -n1 basename | sed 's/^/  - /' || true
    # Namespace index — BundleZpkgResolver maps "Std.IO" → "z42.io.zpkg".
    if [[ -f "$LIBS_DIR/index.json" ]]; then
        cp "$LIBS_DIR/index.json" "$STDLIB_DIR/index.json"
        echo "  - index.json"
    else
        echo "warning: $LIBS_DIR/index.json missing — BundleZpkgResolver will fall back to namespace-as-filename" >&2
    fi
else
    echo "warning: stdlib libs dir not found at $LIBS_DIR" >&2
    echo "         build the standard library first: ./scripts/build-stdlib.sh" >&2
fi

# ── (3) Cargo build × N targets (iOS slice(s) + host extras for swift test). ────

for t in $ALL_TARGETS; do
    echo "cargo build --release --target $t"
    cargo build --release --manifest-path "$RUST_MANIFEST" --target "$t"
done

DEV_LIB="$RUST_TARGET/aarch64-apple-ios/release/$LIB_NAME"
SIM_ARM64_LIB="$RUST_TARGET/aarch64-apple-ios-sim/release/$LIB_NAME"
MAC_LIB="$RUST_TARGET/aarch64-apple-darwin/release/$LIB_NAME"

for f in "$DEV_LIB" "$SIM_ARM64_LIB" "$MAC_LIB"; do
    [[ -f "$f" ]] || { echo "error: expected lib not found: $f" >&2; exit 1; }
done

# ── (4) simulator slice (arm64-only; x86_64-apple-ios dropped per versions.toml). ──
# No lipo needed — single-arch sim slice used directly in xcframework.

SIM_UNIVERSAL="$SIM_ARM64_LIB"

# ── (5) xcodebuild -create-xcframework (ios-device + sim-universal + macos). ──

XCF="$HERE/Z42VM.xcframework"
rm -rf "$XCF"
echo "xcodebuild -create-xcframework -> $XCF"
xcodebuild -create-xcframework \
    -library "$DEV_LIB" \
    -library "$SIM_UNIVERSAL" \
    -library "$MAC_LIB" \
    -output "$XCF"

# ── (6) Compile test fixtures (host z42c → .zbc). ────────────────────────

TEST_FIX="$HERE/Tests/Z42VMTests/Resources/test-fixtures"
mkdir -p "$TEST_FIX"
for src in hello multi_line; do
    src_file="$ROOT/examples/embedding/${src}.z42"
    out_file="$TEST_FIX/${src}.zbc"
    if [[ ! -f "$src_file" ]]; then
        echo "error: fixture source missing: $src_file" >&2
        exit 1
    fi
    echo "z42c $src.z42 → $out_file"
    dotnet "$DRIVER_DLL" "$src_file" --emit zbc -o "$out_file"
done

# ── (7) Stage test bundle stdlib (Bundle.module resource isolation). ─────

TEST_STDLIB="$HERE/Tests/Z42VMTests/Resources/stdlib"
if [[ -d "$LIBS_DIR" ]]; then
    mkdir -p "$TEST_STDLIB"
    cp "$LIBS_DIR"/*.zpkg "$TEST_STDLIB/" 2>/dev/null || true
    [[ -f "$LIBS_DIR/index.json" ]] && cp "$LIBS_DIR/index.json" "$TEST_STDLIB/index.json"
fi

echo ""
echo "built:"
echo "  $XCF/ios-arm64/"
echo "  $XCF/ios-arm64_x86_64-simulator/"
echo "  $XCF/macos-arm64/"
echo "  $STDLIB_DIR/                          (main bundle stdlib)"
echo "  $TEST_FIX/                            (test fixtures)"
echo "  $TEST_STDLIB/                         (test bundle stdlib)"
echo ""
echo "run tests:    cd $HERE && swift test"
echo "consume from Xcode:  .package(path: \"$HERE\")"
