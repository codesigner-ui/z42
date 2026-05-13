#!/usr/bin/env bash
# package_android.sh — Android per-ABI SDK package pipeline.
#
# Args: <pkg_dir> <rid> <version> <profile> <host_rid>
# RID ∈ {android-arm64, android-armv7, android-x64, android-x86}; called
# from scripts/package.sh.
#
# Output (per docs/spec/changes/add-android-package/):
#   bin/README.md
#   libs/                            stdlib zpkg + index.json (cross-package byte-identical)
#   native/
#     libz42_platform_android.a      per-ABI staticlib
#     libz42_platform_android.so     per-ABI cdylib
#     include/                       z42_abi.h + z42_host.h
#   kotlin/io/z42/vm/*.kt            Kotlin facade (cp from platforms/android/.../java/...)
#   cpp/{z42vm_jni.c,CMakeLists.txt,include/} JNI bridge sources
#   examples/hello_c/{main.c,hello.zbc,README.md}
#   manifest.toml

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
source "$SCRIPT_DIR/package_helpers.sh"
source "$ROOT/scripts/_lib/versions.sh"

PKG_DIR="$1"
RID="$2"
VERSION="$3"
PROFILE="$4"
HOST_RID="$5"

CARGO_TARGET=$(rid_to_cargo "$RID")
ABI=$(rid_to_android_abi "$RID")

ANDROID_CRATE="$ROOT/src/toolchain/host/platforms/android/rust"

# ── 0. Tooling check (NDK + cargo-ndk + rust target) ────────────────────

command -v cargo-ndk >/dev/null 2>&1 || {
    echo "error: cargo-ndk not found." >&2
    echo "       install via: cargo install cargo-ndk --locked" >&2
    exit 1
}

NDK_VERSION=$(versions_get build.android.ndk.version)
if [[ -z "${ANDROID_NDK_HOME:-}" ]]; then
    auto_ndk="$ROOT/$(versions_get build.android.install_root)/ndk/$NDK_VERSION"
    if [[ -d "$auto_ndk" ]]; then
        export ANDROID_NDK_HOME="$auto_ndk"
    else
        echo "error: \$ANDROID_NDK_HOME unset and NDK not found at $auto_ndk" >&2
        echo "       run: ./scripts/setup-tools.sh android" >&2
        exit 1
    fi
fi

if ! rustup target list --installed | grep -q "^$CARGO_TARGET$"; then
    echo "error: rustc target $CARGO_TARGET not installed." >&2
    echo "       install via: rustup target add $CARGO_TARGET" >&2
    exit 1
fi

# ── 1. bin/ placeholder ─────────────────────────────────────────────────

echo "[1/8] bin/ placeholder"
pkg_emit_bin_readme_placeholder "$PKG_DIR" "Android"

# ── 2. libz42_platform_android.so (cdylib via cargo-ndk) ────────────────

echo "[2/8] libz42_platform_android.so (cargo ndk -t $ABI, $PROFILE)"
SO_OUT="$PKG_DIR/native"
mkdir -p "$SO_OUT"

(
    cd "$ANDROID_CRATE"
    if [ "$PROFILE" = "release" ]; then
        cargo ndk -t "$ABI" -o "$SO_OUT/.ndk-out" build --release --quiet
    else
        cargo ndk -t "$ABI" -o "$SO_OUT/.ndk-out" build           --quiet
    fi
)

# cargo-ndk lays out as <out>/<abi>/libz42_platform_android.so — flatten.
NDK_OUT_SO="$SO_OUT/.ndk-out/$ABI/libz42_platform_android.so"
if [ ! -f "$NDK_OUT_SO" ]; then
    echo "error: $NDK_OUT_SO not produced" >&2
    exit 1
fi
mv "$NDK_OUT_SO" "$SO_OUT/libz42_platform_android.so"
rm -rf "$SO_OUT/.ndk-out"
echo "      ✓ native/libz42_platform_android.so ($(du -h "$SO_OUT/libz42_platform_android.so" | cut -f1))"

# ── 3. libz42_platform_android.a (staticlib via cargo rustc) ────────────

echo "[3/8] libz42_platform_android.a (cargo rustc --crate-type=staticlib)"
# cargo-ndk insists on a cdylib output (its post-build copy step looks
# for .so); a `--crate-type=staticlib` rustc invocation would make
# cargo-ndk exit non-zero even though the .a was produced. Workaround:
# allow the copy step to fail; we verify the .a exists below.
(
    cd "$ANDROID_CRATE"
    if [ "$PROFILE" = "release" ]; then
        cargo ndk -t "$ABI" -o "$SO_OUT/.ndk-out" rustc --release \
            --lib --crate-type=staticlib --quiet >/dev/null 2>&1 || true
    else
        cargo ndk -t "$ABI" -o "$SO_OUT/.ndk-out" rustc \
            --lib --crate-type=staticlib --quiet >/dev/null 2>&1 || true
    fi
)

# Locate the .a: cargo emits to target/<triple>/<profile>/libz42_platform_android.a
STATIC_A="$ROOT/artifacts/build/runtime/$CARGO_TARGET/$PROFILE/libz42_platform_android.a"
if [ ! -f "$STATIC_A" ]; then
    # Fallback: cargo-ndk may put intermediate output in default target/ dir.
    STATIC_A="$ANDROID_CRATE/target/$CARGO_TARGET/$PROFILE/libz42_platform_android.a"
fi
if [ ! -f "$STATIC_A" ]; then
    echo "error: libz42_platform_android.a not produced for $CARGO_TARGET" >&2
    exit 1
fi
cp "$STATIC_A" "$SO_OUT/libz42_platform_android.a"
rm -rf "$SO_OUT/.ndk-out"
echo "      ✓ native/libz42_platform_android.a ($(du -h "$SO_OUT/libz42_platform_android.a" | cut -f1))"

# ── 4. C ABI headers ────────────────────────────────────────────────────

echo "[4/8] C ABI headers"
pkg_copy_native_includes "$PKG_DIR"

# ── 5. Kotlin facade sources ────────────────────────────────────────────

echo "[5/8] kotlin/io/z42/vm/*.kt"
pkg_emit_kotlin_sources "$PKG_DIR"
echo "      ✓ kotlin/io/z42/vm/ ($(ls "$PKG_DIR/kotlin/io/z42/vm/" | wc -l | tr -d ' ') files)"

# ── 6. JNI bridge sources ───────────────────────────────────────────────

echo "[6/8] cpp/{z42vm_jni.c,CMakeLists.txt}"
pkg_emit_jni_bridge "$PKG_DIR"
echo "      ✓ cpp/"

# ── 7. libs/ + examples/hello_c ─────────────────────────────────────────

echo "[7/8] stdlib + examples/hello_c"
pkg_copy_libs "$PKG_DIR"
pkg_emit_examples_hello_c "$PKG_DIR" "$ROOT/examples/embedding/hello_c/README.md.android"
echo "      ✓ libs/ + examples/hello_c/"

# ── 8. manifest.toml ────────────────────────────────────────────────────

echo "[8/8] manifest.toml"
pkg_emit_manifest "$PKG_DIR" "$RID" "$VERSION" "$PROFILE" "$HOST_RID"
echo "      ✓ manifest.toml"
