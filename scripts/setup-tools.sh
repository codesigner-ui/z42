#!/usr/bin/env bash
# scripts/setup-tools.sh — install/verify the toolchain declared in versions.toml.
#
# Idempotent: existing tools at the right version are no-op.
#
# Usage:
#   ./scripts/setup-tools.sh                      # install all missing (cross-platform + every [build.*])
#   ./scripts/setup-tools.sh <platform>           # only android / ios / wasm
#   ./scripts/setup-tools.sh --check              # verify only, never install
#   ./scripts/setup-tools.sh --drift              # check Package.swift / build.gradle.kts agree with versions.toml
#   ./scripts/setup-tools.sh android --print-env  # emit sourceable env exports (e.g. ANDROID_NDK_HOME)
#
# Source-of-truth = versions.toml. See file head there for schema.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/scripts/_lib/versions.sh"
versions_require_python3

MODE="install"
PLATFORM=""
for arg in "$@"; do
    case "$arg" in
        --check)     MODE="check"     ;;
        --drift)     MODE="drift"     ;;
        --print-env) MODE="print-env" ;;
        android|ios|wasm) PLATFORM="$arg" ;;
        -h|--help) sed -n '2,16p' "$0" | sed 's/^# \?//'; exit 0 ;;
        *) echo "unknown arg: $arg (try --help)" >&2; exit 2 ;;
    esac
done

PLATFORMS=("android" "ios" "wasm")
[[ -n "$PLATFORM" ]] && PLATFORMS=("$PLATFORM")

# ── helpers ───────────────────────────────────────────────────────────────

detect_os() {
    case "$(uname -s)" in
        Darwin) echo "darwin" ;;
        Linux)  echo "linux" ;;
        *) echo "error: unsupported host OS for tool download: $(uname -s)" >&2; return 1 ;;
    esac
}

ensure_rust_target() {
    local t="$1"
    if rustup target list --installed 2>/dev/null | grep -q "^$t$"; then
        echo "  ✓ rust target $t"
    elif [[ "$MODE" == "check" ]]; then
        echo "  ✗ rust target $t (missing)" >&2; return 1
    else
        echo "  rustup target add $t"; rustup target add "$t"
    fi
}

ensure_cargo_install() {
    local crate="$1" bin="${2:-$1}"
    if command -v "$bin" >/dev/null 2>&1; then
        echo "  ✓ $bin"
    elif [[ "$MODE" == "check" ]]; then
        echo "  ✗ $bin (missing; cargo install $crate --locked)" >&2; return 1
    else
        echo "  cargo install $crate --locked"; cargo install "$crate" --locked
    fi
}

# ── android ───────────────────────────────────────────────────────────────

ndk_install_dir() {
    echo "$ROOT/$(versions_get build.android.install_root)/ndk/$(versions_get build.android.ndk.version)"
}

resolve_ndk_home() {
    local local_install
    local_install="$(ndk_install_dir)"
    if [[ -d "$local_install" ]]; then echo "$local_install"
    elif [[ -n "${ANDROID_NDK_HOME:-}" && -d "$ANDROID_NDK_HOME" ]]; then echo "$ANDROID_NDK_HOME"
    else return 1
    fi
}

install_ndk() {
    local ver url_tmpl want_sha os url tmp_zip unpack target_dir got_sha
    ver=$(versions_get build.android.ndk.version)
    url_tmpl=$(versions_get build.android.ndk.url)
    want_sha=$(versions_get build.android.ndk.sha256)
    target_dir="$(ndk_install_dir)"
    os=$(detect_os)
    url="${url_tmpl//\{os\}/$os}"

    tmp_zip=$(mktemp -t ndk-XXXXX.zip)
    trap 'rm -f "$tmp_zip"' EXIT
    echo "  GET $url"
    curl -L --fail --progress-bar -o "$tmp_zip" "$url"

    got_sha=$(shasum -a 256 "$tmp_zip" | awk '{print $1}')
    if [[ -n "$want_sha" ]]; then
        [[ "$got_sha" == "$want_sha" ]] || {
            echo "error: NDK sha256 mismatch (want=$want_sha got=$got_sha)" >&2; exit 1; }
        echo "  ✓ sha256 verified"
    else
        echo "  ⚠ versions.toml [build.android.ndk].sha256 is empty"
        echo "    add to versions.toml:  sha256 = \"$got_sha\""
    fi

    mkdir -p "$(dirname "$target_dir")"
    unpack=$(mktemp -d)
    echo "  unzip → $target_dir"
    unzip -q "$tmp_zip" -d "$unpack"
    # NDK zip contains one top dir like android-ndk-r26d/
    mv "$unpack"/android-ndk-*/ "$target_dir"
    rm -rf "$unpack"
    echo "  ✓ NDK $ver installed"
}

setup_android() {
    echo "── android ──"
    if [[ "$MODE" == "print-env" ]]; then
        local home
        home=$(resolve_ndk_home) || { echo "# android: NDK not found; run ./scripts/setup-tools.sh android" >&2; return 1; }
        echo "export ANDROID_NDK_HOME=\"$home\""
        return
    fi
    if [[ "$MODE" == "drift" ]]; then check_android_drift; return; fi

    for t in $(versions_get_list platform.android.rust_targets); do ensure_rust_target "$t"; done
    ensure_cargo_install cargo-ndk

    # JDK (verify-only — system install)
    local jdk_min
    jdk_min=$(versions_get build.android.jdk_min)
    if command -v javac >/dev/null 2>&1; then
        local jdk_major
        jdk_major=$(javac -version 2>&1 | awk '{print $2}' | cut -d. -f1)
        if (( jdk_major < jdk_min )); then
            echo "  ✗ JDK $jdk_major < required $jdk_min" >&2
        else
            echo "  ✓ JDK $jdk_major (≥ $jdk_min)"
        fi
    else
        echo "  ✗ javac not on PATH (need JDK $jdk_min+)" >&2
    fi

    # NDK
    if resolve_ndk_home >/dev/null; then
        echo "  ✓ NDK $(versions_get build.android.ndk.version) at $(resolve_ndk_home)"
    elif [[ "$MODE" == "check" ]]; then
        echo "  ✗ NDK $(versions_get build.android.ndk.version) missing (expected $(ndk_install_dir))" >&2
    else
        install_ndk
    fi
}

check_android_drift() {
    local want_min want_target build_gradle min_actual compile_actual
    want_min=$(versions_get platform.android.min_api)
    want_target=$(versions_get platform.android.target_api)
    build_gradle="$ROOT/src/toolchain/host/platforms/android/z42vm/build.gradle.kts"
    min_actual=$(grep -E "^[[:space:]]*minSdk[[:space:]]*=" "$build_gradle" | grep -oE "[0-9]+" | head -1)
    compile_actual=$(grep -E "^[[:space:]]*compileSdk[[:space:]]*=" "$build_gradle" | grep -oE "[0-9]+" | head -1)
    [[ "$min_actual"     == "$want_min"    ]] && echo "  ✓ build.gradle.kts minSdk=$min_actual = versions.toml min_api"           || echo "  ✗ drift: build.gradle.kts minSdk=$min_actual ≠ versions.toml min_api=$want_min" >&2
    [[ "$compile_actual" == "$want_target" ]] && echo "  ✓ build.gradle.kts compileSdk=$compile_actual = versions.toml target_api" || echo "  ✗ drift: build.gradle.kts compileSdk=$compile_actual ≠ versions.toml target_api=$want_target" >&2
}

# ── ios ───────────────────────────────────────────────────────────────────

setup_ios() {
    echo "── ios ──"
    if [[ "$MODE" == "print-env" ]]; then return; fi
    if [[ "$MODE" == "drift" ]]; then check_ios_drift; return; fi

    for t in $(versions_get_list platform.ios.rust_targets) $(versions_get_list build.ios.extra_rust_targets); do
        ensure_rust_target "$t"
    done

    local xcode_min
    xcode_min=$(versions_get build.ios.xcode_min)
    if command -v xcodebuild >/dev/null 2>&1; then
        local xcode_actual
        xcode_actual=$(xcodebuild -version 2>/dev/null | head -1 | awk '{print $2}')
        echo "  ✓ Xcode $xcode_actual (want ≥ $xcode_min)"
    else
        echo "  ✗ xcodebuild missing (run: xcode-select --install)" >&2
    fi
}

check_ios_drift() {
    local want_ios want_macos pkg ios_actual macos_actual
    want_ios=$(versions_get platform.ios.min_ios)
    want_macos=$(versions_get platform.ios.min_macos)
    pkg="$ROOT/src/toolchain/host/platforms/ios/Package.swift"
    ios_actual=$(grep -oE '\.iOS\(\.v[0-9]+\)' "$pkg" | grep -oE '[0-9]+' | head -1)
    macos_actual=$(grep -oE '\.macOS\(\.v[0-9]+\)' "$pkg" | grep -oE '[0-9]+' | head -1)
    [[ "$ios_actual"   == "${want_ios%%.*}"   ]] && echo "  ✓ Package.swift .iOS(.v$ios_actual) = versions.toml min_ios=$want_ios"     || echo "  ✗ drift: Package.swift .iOS(.v$ios_actual) ≠ versions.toml min_ios=$want_ios" >&2
    [[ "$macos_actual" == "${want_macos%%.*}" ]] && echo "  ✓ Package.swift .macOS(.v$macos_actual) = versions.toml min_macos=$want_macos" || echo "  ✗ drift: Package.swift .macOS(.v$macos_actual) ≠ versions.toml min_macos=$want_macos" >&2
}

# ── wasm ──────────────────────────────────────────────────────────────────

setup_wasm() {
    echo "── wasm ──"
    if [[ "$MODE" == "print-env" ]]; then return; fi
    if [[ "$MODE" == "drift" ]]; then echo "  (wasm has no native-file drift to check)"; return; fi

    for t in $(versions_get_list platform.wasm.rust_targets); do ensure_rust_target "$t"; done
    ensure_cargo_install wasm-pack
}

# ── main ──────────────────────────────────────────────────────────────────

if [[ "$MODE" != "print-env" && "$MODE" != "drift" ]]; then
    echo "── toolchain (cross-platform) ──"
    versions_check_rust
    versions_check_dotnet
    versions_check_node
fi

for plat in "${PLATFORMS[@]}"; do
    case "$plat" in
        android) setup_android ;;
        ios)     setup_ios     ;;
        wasm)    setup_wasm    ;;
    esac
done

if [[ "$MODE" == "install" ]]; then
    echo ""
    echo "✅ setup complete"
    echo "   For ANDROID_NDK_HOME export, run:"
    echo "     eval \"\$(./scripts/setup-tools.sh android --print-env)\""
fi
