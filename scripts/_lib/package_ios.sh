#!/usr/bin/env bash
# package_ios.sh — iOS slice SDK package pipeline.
#
# Args: <pkg_dir> <rid> <version> <profile> <host_rid>
# RID ∈ {ios-arm64, ios-arm64-sim}; called from scripts/package.sh.
#
# Output (per docs/spec/archive/2026-05-13-define-package-layout/):
#   bin/README.md
#   libs/                       stdlib zpkg + index.json (cross-package byte-identical)
#   native/
#     libz42.a                  iOS slice staticlib (cargo --target <ios-*> --crate-type=staticlib)
#     Z42VM.xcframework/        single-slice xcframework wrapping libz42.a
#     include/                  z42_abi.h + z42_host.h
#   Sources/Z42VM/*.swift       Swift facade (cp from platforms/ios/Sources/)
#   Sources/Z42VMC/             clang module
#   Package.swift               SwiftPM manifest consuming the single-slice xcframework
#   examples/hello_c/{main.c,hello.zbc,README.md}
#   manifest.toml

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
source "$SCRIPT_DIR/package_helpers.sh"

PKG_DIR="$1"
RID="$2"
VERSION="$3"
PROFILE="$4"
HOST_RID="$5"

CARGO_TARGET=$(rid_to_cargo "$RID")

# fix-ios-deployment-target (2026-05-27): IPHONEOS_DEPLOYMENT_TARGET must be
# exported so cargo passes it to the clang linker; without it the linker
# defaults to iOS 10.0 and libz-ng-sys's crc32_chorba references
# ___chkstk_darwin (available since iOS 13.0) become undefined.
IOS_DEPLOY="16.0"
export IPHONEOS_DEPLOYMENT_TARGET="$IOS_DEPLOY"

# ── 1. bin/ placeholder ─────────────────────────────────────────────────

echo "[1/7] bin/ placeholder"
pkg_emit_bin_readme_placeholder "$PKG_DIR" "iOS"

# ── 2. libz42.a (iOS slice staticlib) ───────────────────────────────────

echo "[2/7] libz42.a (cargo $CARGO_TARGET, $PROFILE)"
cargo rustc \
    $([ "$PROFILE" = "release" ] && echo "--release") \
    --lib --crate-type=staticlib \
    --manifest-path "$ROOT/src/runtime/Cargo.toml" \
    --target "$CARGO_TARGET" \
    --no-default-features --features "ios" \
    --quiet >/dev/null 2>&1 || {
        echo "error: cargo rustc staticlib for $CARGO_TARGET failed" >&2
        # Re-run verbose so user sees the actual error.
        cargo rustc \
            $([ "$PROFILE" = "release" ] && echo "--release") \
            --lib --crate-type=staticlib \
            --manifest-path "$ROOT/src/runtime/Cargo.toml" \
            --target "$CARGO_TARGET" \
            --no-default-features --features "ios" 2>&1 | tail -20
        exit 1
    }

local_lib="$ROOT/artifacts/build/runtime/$CARGO_TARGET/$PROFILE/libz42.a"
[ -f "$local_lib" ] || { echo "error: $local_lib not produced" >&2; exit 1; }
cp "$local_lib" "$PKG_DIR/native/libz42.a"
echo "      ✓ native/libz42.a ($(du -h "$PKG_DIR/native/libz42.a" | cut -f1))"

# ── 2b. libz42_compression.a (iOS staticlib, add-z42-compression 2026-05-22) ──
# iOS App Store disallows dlopen of arbitrary dylibs, so we ship the
# compression code as a staticlib only. Integrators either link the .a
# directly (Xcode → Build Phases → Link Binary With Libraries) or via
# the xcframework slice produced in step 4. The z42 runtime side
# auto-enables the `bundled-compression` Cargo feature on iOS, which
# statically registers the rlib's builtins at VmContext::new() instead
# of scanning for a dlopen target.

echo "[2b/7] libz42_compression.a (cargo $CARGO_TARGET, $PROFILE)"
cargo build \
    $([ "$PROFILE" = "release" ] && echo "--release") \
    -p z42-compression \
    --manifest-path "$ROOT/src/runtime/Cargo.toml" \
    --target "$CARGO_TARGET" --quiet
compression_lib="$ROOT/artifacts/build/runtime/$CARGO_TARGET/$PROFILE/libz42_compression.a"
[ -f "$compression_lib" ] || { echo "error: $compression_lib not produced" >&2; exit 1; }
cp "$compression_lib" "$PKG_DIR/native/libz42_compression.a"
echo "      ✓ native/libz42_compression.a ($(du -h "$PKG_DIR/native/libz42_compression.a" | cut -f1))"

# ── 3. C ABI headers (needed before xcframework) ────────────────────────

echo "[3/7] C ABI headers"
pkg_copy_native_includes "$PKG_DIR"

# ── 4. Z42VM.xcframework (single slice) ─────────────────────────────────

echo "[4/7] Z42VM.xcframework (single slice)"
pkg_emit_ios_xcframework "$PKG_DIR" "$CARGO_TARGET"
echo "      ✓ native/Z42VM.xcframework"

# ── 5. Sources/ + Package.swift ─────────────────────────────────────────

echo "[5/7] Sources/Z42VM + Package.swift"
pkg_emit_ios_facade "$PKG_DIR" "$RID"
echo "      ✓ Sources/ + Package.swift"

# ── 6. libs/ + examples/hello_c ─────────────────────────────────────────

echo "[6/7] stdlib + examples/hello_c"
pkg_copy_libs "$PKG_DIR"
pkg_emit_examples_hello_c "$PKG_DIR" "$ROOT/examples/embedding/hello_c/README.md.ios"
echo "      ✓ libs/ + examples/hello_c/"

# ── 7. manifest.toml ────────────────────────────────────────────────────

echo "[7/7] manifest.toml"
pkg_emit_manifest "$PKG_DIR" "$RID" "$VERSION" "$PROFILE" "$HOST_RID"
echo "      ✓ manifest.toml"
