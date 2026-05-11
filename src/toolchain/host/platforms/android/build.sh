#!/usr/bin/env bash
# Build `z42vm-release.aar` end-to-end:
#   1. Verify tooling (cargo-ndk + 4 android rust targets + JDK 17 + $ANDROID_NDK_HOME).
#   2. Copy stdlib zpkgs from artifacts/build/libs/release/ → z42vm/src/main/assets/stdlib/.
#   3. cargo ndk × 4 ABI in release; outputs land under z42vm/src/main/jniLibs/<abi>/.
#   4. ./gradlew :z42vm:assembleRelease (compiles Kotlin + JNI .so via CMake + packages AAR).
#
# Spec: docs/spec/archive/2026-05-12-add-platform-android/

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../../../.." && pwd)"
RUST_MANIFEST="$HERE/rust/Cargo.toml"

# ── (1) Tooling check (fail-fast). ───────────────────────────────────────

command -v cargo >/dev/null 2>&1 || {
    echo "error: cargo not found. Install via https://rustup.rs" >&2; exit 1; }
command -v cargo-ndk >/dev/null 2>&1 || {
    echo "error: cargo-ndk not found." >&2
    echo "       install via: cargo install cargo-ndk --locked" >&2; exit 1; }

if [[ -z "${ANDROID_NDK_HOME:-}" ]]; then
    echo "error: \$ANDROID_NDK_HOME is unset." >&2
    echo "       install Android NDK (Side by side) 26+ via Android Studio SDK Manager," >&2
    echo "       then export ANDROID_NDK_HOME=\$ANDROID_HOME/ndk/<version>" >&2
    exit 1
fi
if [[ ! -d "$ANDROID_NDK_HOME" ]]; then
    echo "error: \$ANDROID_NDK_HOME=$ANDROID_NDK_HOME does not exist." >&2; exit 1
fi
for t in aarch64-linux-android armv7-linux-androideabi x86_64-linux-android i686-linux-android; do
    if ! rustup target list --installed | grep -q "^$t$"; then
        echo "error: rustc target $t not installed." >&2
        echo "       install via: rustup target add $t" >&2; exit 1
    fi
done

# Gradle wrapper expected; if not present we attempt `gradle wrapper` if
# a system `gradle` is on PATH. AGP requires Gradle 8.6+.
if [[ ! -x "$HERE/gradlew" ]]; then
    if command -v gradle >/dev/null 2>&1; then
        echo "no gradle wrapper found; generating one with system gradle"
        (cd "$HERE" && gradle wrapper --gradle-version 8.6)
    else
        echo "error: $HERE/gradlew not present and no system 'gradle' on PATH." >&2
        echo "       open this project once in Android Studio (which generates" >&2
        echo "       gradlew automatically), or install gradle and re-run build.sh." >&2
        exit 1
    fi
fi

# ── (2) Stdlib bundle. ───────────────────────────────────────────────────

LIBS_DIR="$ROOT/artifacts/build/libs/release"
STDLIB_DIR="$HERE/z42vm/src/main/assets/stdlib"

if [[ -d "$LIBS_DIR" ]]; then
    echo "copying stdlib zpkgs from $LIBS_DIR"
    mkdir -p "$STDLIB_DIR"
    cp "$LIBS_DIR"/*.zpkg "$STDLIB_DIR/" 2>/dev/null || true
    ls "$STDLIB_DIR"/*.zpkg 2>/dev/null | xargs -n1 basename | sed 's/^/  - /' || true
else
    echo "warning: stdlib libs dir not found at $LIBS_DIR" >&2
    echo "         build the standard library first: dotnet build src/compiler/z42.slnx" >&2
fi

# ── (3) cargo-ndk × 4 ABI. ──────────────────────────────────────────────

echo "cargo ndk: building 4 ABI"
cargo ndk \
    -t arm64-v8a -t armeabi-v7a -t x86_64 -t x86 \
    -o "$HERE/z42vm/src/main/jniLibs" \
    build --release --manifest-path "$RUST_MANIFEST"

# ── (4) Gradle assemble. ────────────────────────────────────────────────

echo "gradle :z42vm:assembleRelease"
cd "$HERE"
./gradlew :z42vm:assembleRelease

AAR="$HERE/z42vm/build/outputs/aar/z42vm-release.aar"
if [[ -f "$AAR" ]]; then
    echo ""
    echo "built:"
    echo "  $AAR"
    echo "  $HERE/z42vm/src/main/jniLibs/{arm64-v8a,armeabi-v7a,x86_64,x86}/libz42_platform_android.so"
    echo "  $STDLIB_DIR/"
else
    echo "error: expected AAR not found at $AAR" >&2; exit 1
fi
