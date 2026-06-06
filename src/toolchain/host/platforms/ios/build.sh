#!/usr/bin/env bash
# Build `Z42VM.xcframework` end-to-end and stage test bundle resources:
#   1. Verify tooling (xcodebuild + 4 rust targets: 3 iOS + 1 macOS-arm64).
#   2. Copy stdlib zpkgs from artifacts/build/libraries/dist/release/ into Resources/stdlib/.
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

# 单一真相源：repo 根 versions.toml。scripts/_lib/versions.sh 已随 scripts→xtask
# 迁移删除；这里内联一个最小 tomllib 读取器（python3 本就是构建前置）。值仍单一
# 源自 versions.toml。版本下限改为 presence-only 守卫（原 compare 仅 warning）。
command -v python3 >/dev/null 2>&1 || { echo "error: python3 required to read versions.toml" >&2; exit 1; }
command -v rustc   >/dev/null 2>&1 || echo "warning: rustc not found (https://rustup.rs)" >&2
command -v dotnet  >/dev/null 2>&1 || echo "warning: dotnet not found (.NET 10+)" >&2
_vget()  { python3 -c "import tomllib;d=tomllib.load(open('$ROOT/versions.toml','rb'))
for p in '$1'.split('.'): d=d[p]
print(d)"; }
_vlist() { python3 -c "import tomllib;d=tomllib.load(open('$ROOT/versions.toml','rb'))
for p in '$1'.split('.'): d=d[p]
print(' '.join(map(str,d)))"; }

IOS_RUST_TARGETS=$(_vlist platform.ios.rust_targets)
HOST_EXTRA=$(_vlist build.ios.extra_rust_targets)
XCODE_MIN=$(_vget build.ios.xcode_min)
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
    echo "error: dotnet not found. Install .NET 10+ (https://dotnet.microsoft.com)." >&2
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

LIBS_DIR="$ROOT/artifacts/build/libraries/dist/release"
STDLIB_DIR="$HERE/Resources/stdlib"

if [[ -d "$LIBS_DIR" ]]; then
    echo "copying stdlib zpkgs from $LIBS_DIR"
    mkdir -p "$STDLIB_DIR"
    cp "$LIBS_DIR"/*.zpkg "$STDLIB_DIR/" 2>/dev/null || true
    ls "$STDLIB_DIR"/*.zpkg 2>/dev/null | xargs -n1 basename | sed 's/^/  - /' || true
    # No namespace index is shipped: the embedding host injects each zpkg
    # (z42_host_add_zpkg) and the runtime reads its NSPC section to map
    # namespaces. See docs/spec/archive/2026-06-06-drop-index-json-self-describing/.
else
    echo "warning: stdlib libs dir not found at $LIBS_DIR" >&2
    echo "         build the standard library first: z42 xtask.zpkg build stdlib" >&2
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
    rm -f "$TEST_STDLIB/index.json"   # no namespace index — BundleZpkgResolver reads NSPC
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
