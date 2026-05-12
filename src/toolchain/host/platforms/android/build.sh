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

# 单一真相源：repo 根 versions.toml；helper 在 scripts/_lib/versions.sh.
source "$ROOT/scripts/_lib/versions.sh"
versions_require_python3
versions_check_rust          # 校 rustc 版本 vs [toolchain.rust]

NDK_VERSION=$(versions_get build.android.ndk.version)
RUST_TARGETS=$(versions_get_list platform.android.rust_targets)
ABIS=$(versions_get_list platform.android.abis)
JDK_MIN=$(versions_get build.android.jdk_min)

# ── (1) Tooling check (fail-fast). ───────────────────────────────────────

command -v cargo >/dev/null 2>&1 || {
    echo "error: cargo not found. Install via https://rustup.rs" >&2; exit 1; }
command -v cargo-ndk >/dev/null 2>&1 || {
    echo "error: cargo-ndk not found." >&2
    echo "       install via: cargo install cargo-ndk --locked" >&2; exit 1; }

# Auto-discover NDK from artifacts/tools/ (where setup-tools.sh installs it)
# when $ANDROID_NDK_HOME is unset. Versioned subdir per versions.toml NDK pin.
if [[ -z "${ANDROID_NDK_HOME:-}" ]]; then
    auto_ndk="$ROOT/$(versions_get build.android.install_root)/ndk/$NDK_VERSION"
    if [[ -d "$auto_ndk" ]]; then
        export ANDROID_NDK_HOME="$auto_ndk"
        echo "ANDROID_NDK_HOME auto-detected: $ANDROID_NDK_HOME"
    else
        echo "error: \$ANDROID_NDK_HOME unset and NDK not found locally." >&2
        echo "       z42 pins NDK $NDK_VERSION (versions.toml [build.android.ndk])." >&2
        echo "       fix one of:" >&2
        echo "         1) ./scripts/setup-tools.sh android        # download to artifacts/tools/" >&2
        echo "         2) export ANDROID_NDK_HOME=<your-ndk-path>  # use existing install" >&2
        exit 1
    fi
fi
if [[ ! -d "$ANDROID_NDK_HOME" ]]; then
    echo "error: \$ANDROID_NDK_HOME=$ANDROID_NDK_HOME does not exist." >&2; exit 1
fi
for t in $RUST_TARGETS; do
    if ! rustup target list --installed | grep -q "^$t$"; then
        echo "error: rustc target $t not installed (declared in versions.toml [platform.android].rust_targets)." >&2
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
    # Namespace index — AssetZpkgResolver maps "Std.IO" → "z42.io.zpkg".
    if [[ -f "$LIBS_DIR/index.json" ]]; then
        cp "$LIBS_DIR/index.json" "$STDLIB_DIR/index.json"
        echo "  - index.json"
    else
        echo "warning: $LIBS_DIR/index.json missing — AssetZpkgResolver will fall back to namespace-as-filename" >&2
    fi
else
    echo "warning: stdlib libs dir not found at $LIBS_DIR" >&2
    echo "         build the standard library first: ./scripts/build-stdlib.sh" >&2
fi

# ── (3) cargo-ndk × 4 ABI. ──────────────────────────────────────────────

echo "cargo ndk: building $(echo $ABIS | wc -w | tr -d ' ') ABI ($ABIS)"
# cargo-ndk auto-discovers Cargo.toml from cwd (it doesn't honor
# --manifest-path the way `cargo build` does), so cd into the crate dir.
# ABIs come from versions.toml [platform.android].abis — gradle naming
# (cargo-ndk's `-t` flag accepts the same set).
abi_args=()
for abi in $ABIS; do abi_args+=(-t "$abi"); done
(
    cd "$HERE/rust"
    cargo ndk "${abi_args[@]}" \
        -o "$HERE/z42vm/src/main/jniLibs" \
        build --release
)

# ── (3.5) Compile test fixtures (shared with add-ios-tests / add-wasm-tests). ─

DRIVER_DLL="$ROOT/artifacts/build/compiler/z42.Driver/bin/z42c.dll"
TEST_FIX="$HERE/z42vm/src/androidTest/assets/test-fixtures"
if [[ -f "$DRIVER_DLL" ]]; then
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
else
    echo "warning: z42c driver missing — skip test fixture compile" >&2
    echo "         build the compiler first: dotnet build $ROOT/src/compiler/z42.slnx" >&2
fi

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
    echo "  $TEST_FIX/                  (test fixtures for ./test.sh)"
else
    echo "error: expected AAR not found at $AAR" >&2; exit 1
fi
