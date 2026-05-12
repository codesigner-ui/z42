#!/usr/bin/env bash
# install-android-toolchain-local.sh — Install Android SDK + NDK + emulator
# + Gradle locally into `artifacts/tools/`, without touching the system.
# Idempotent; pass --force to redownload.
#
# Installs:
#   - cmdline-tools (sdkmanager + avdmanager) — entry point
#   - platforms;android-34, build-tools;34.0.0, platform-tools, emulator
#   - system-images;android-34;google_apis_playstore;arm64-v8a (Apple silicon)
#   - NDK r26.3 (matches Z42 Android facade's cargo-ndk target)
#   - AVD "z42_pixel6_api34" (Pixel 6, API 34 arm64) — for instrumented tests
#   - Gradle 8.7 (AGP 8.6 compatible) — used by platforms/android/build.sh
#
# Total disk: ~4 GB. Total time: ~10-15 min on good internet.
#
# Prereqs:
#   - JDK 17+ on system (macOS bundles 21 or newer; brew install --cask temurin)
#   - curl, unzip
#
# After install, env vars to use:
#   export ANDROID_HOME="$PWD/artifacts/tools/android-sdk"
#   export ANDROID_NDK_HOME="$PWD/artifacts/tools/android-ndk"
#   export PATH="$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator:$PATH"
#
# (the script prints this block at the end for copy-paste)

set -euo pipefail

# ── Versions (pinned for reproducibility). ───────────────────────────────

CMDLINE_TOOLS_VERSION="13114758"          # 2025-01 release; latest as of 2026-05
NDK_VERSION="26.3.11579264"               # NDK r26d
PLATFORM_VERSION="android-34"
BUILD_TOOLS_VERSION="34.0.0"
GRADLE_VERSION="8.7"                      # AGP 8.6 compatible
SYSTEM_IMAGE_PKG="system-images;${PLATFORM_VERSION};google_apis_playstore;arm64-v8a"
AVD_NAME="z42_pixel6_api34"
AVD_DEVICE="pixel_6"

# ── Paths. ────────────────────────────────────────────────────────────────

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS_DIR="$ROOT/artifacts/tools"
SDK_DIR="$TOOLS_DIR/android-sdk"
NDK_DIR="$TOOLS_DIR/android-ndk"
GRADLE_DIR="$TOOLS_DIR/gradle"

FORCE=false
for arg in "$@"; do
    case "$arg" in
        --force) FORCE=true ;;
        -h|--help)
            sed -n '2,24p' "$0" | sed 's/^# \?//'
            exit 0
            ;;
        *)
            echo "error: unknown arg: $arg" >&2
            exit 2
            ;;
    esac
done

# ── Platform detection. ──────────────────────────────────────────────────

case "$(uname -s)" in
    Darwin) HOST_OS="mac" ;;
    Linux)  HOST_OS="linux" ;;
    *)      echo "error: unsupported OS $(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
    arm64|aarch64) HOST_ARCH="arm64" ;;
    x86_64)        HOST_ARCH="x86_64" ;;
    *)             echo "error: unsupported arch $(uname -m)" >&2; exit 1 ;;
esac

# ── JDK check. ───────────────────────────────────────────────────────────

if [[ -z "${JAVA_HOME:-}" ]] && [[ -x "/usr/libexec/java_home" ]]; then
    export JAVA_HOME="$(/usr/libexec/java_home -v 17+ 2>/dev/null || /usr/libexec/java_home 2>/dev/null || true)"
fi
if [[ -z "${JAVA_HOME:-}" || ! -d "$JAVA_HOME" ]]; then
    echo "error: JDK 17+ not found. Install via 'brew install --cask temurin@17' or use macOS bundled java." >&2
    exit 1
fi
JAVA_MAJOR=$("$JAVA_HOME/bin/java" -version 2>&1 | awk -F\" '/version/ {print $2}' | awk -F. '{print ($1 == "1" ? $2 : $1)}')
if (( JAVA_MAJOR < 17 )); then
    echo "error: JDK $JAVA_MAJOR detected at $JAVA_HOME; need 17+." >&2
    exit 1
fi
echo "using JDK $JAVA_MAJOR at $JAVA_HOME"

# ── tooling check. ───────────────────────────────────────────────────────

for tool in curl unzip; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "error: $tool not found." >&2; exit 1;
    }
done

mkdir -p "$TOOLS_DIR"

# ── (1) cmdline-tools (sdkmanager / avdmanager). ─────────────────────────

CMDLINE_DIR="$SDK_DIR/cmdline-tools/latest"
SDKMANAGER="$CMDLINE_DIR/bin/sdkmanager"
AVDMANAGER="$CMDLINE_DIR/bin/avdmanager"

if [[ -x "$SDKMANAGER" && "$FORCE" = false ]]; then
    echo "[1/5] cmdline-tools already installed at $CMDLINE_DIR (skip)"
else
    echo "[1/5] downloading cmdline-tools ($CMDLINE_TOOLS_VERSION)"
    rm -rf "$SDK_DIR/cmdline-tools"
    mkdir -p "$SDK_DIR/cmdline-tools"
    TMP=$(mktemp -d)
    trap 'rm -rf "$TMP"' EXIT
    URL="https://dl.google.com/android/repository/commandlinetools-${HOST_OS}-${CMDLINE_TOOLS_VERSION}_latest.zip"
    curl --fail --location --silent --show-error -o "$TMP/cmdline-tools.zip" "$URL"
    unzip -q "$TMP/cmdline-tools.zip" -d "$TMP"
    # The zip extracts to "cmdline-tools/" — we want it renamed to "latest/".
    mv "$TMP/cmdline-tools" "$CMDLINE_DIR"
    [[ -x "$SDKMANAGER" ]] || { echo "error: $SDKMANAGER missing after extract" >&2; exit 1; }
fi

# ── Accept licenses (yes pipe; idempotent). ──────────────────────────────

echo "[2/5] accepting SDK licenses"
yes | "$SDKMANAGER" --sdk_root="$SDK_DIR" --licenses >/dev/null 2>&1 || true

# ── (3) SDK packages via sdkmanager (idempotent). ────────────────────────

PACKAGES=(
    "platforms;${PLATFORM_VERSION}"
    "build-tools;${BUILD_TOOLS_VERSION}"
    "platform-tools"
    "emulator"
    "$SYSTEM_IMAGE_PKG"
)

echo "[3/5] installing SDK packages (${#PACKAGES[@]} components)"
"$SDKMANAGER" --sdk_root="$SDK_DIR" --install "${PACKAGES[@]}"

# ── (4) NDK (separate from SDK proper for cargo-ndk). ────────────────────

# NDK lives under sdk_root/ndk/<version>/ when installed via sdkmanager.
# We additionally symlink it into ndk-tools side dir for ANDROID_NDK_HOME
# pointing at a stable path (avoids hardcoding version in callers).

echo "[4/5] installing NDK r${NDK_VERSION%%.*}"
"$SDKMANAGER" --sdk_root="$SDK_DIR" --install "ndk;${NDK_VERSION}"

NDK_INSTALL="$SDK_DIR/ndk/$NDK_VERSION"
[[ -d "$NDK_INSTALL" ]] || { echo "error: NDK install dir $NDK_INSTALL missing" >&2; exit 1; }

# Symlink to stable path (callers use $ANDROID_NDK_HOME without version).
rm -f "$NDK_DIR"
ln -s "$NDK_INSTALL" "$NDK_DIR"

# ── (5) AVD (Pixel 6, API 34 arm64). ─────────────────────────────────────

# Allow re-running without colliding with an existing AVD.
EXISTING_AVD=$("$AVDMANAGER" list avd 2>/dev/null | awk -v want="$AVD_NAME" '/Name:/ && $2==want {print $2}' || true)

if [[ -n "$EXISTING_AVD" && "$FORCE" = false ]]; then
    echo "[5/5] AVD $AVD_NAME already exists (skip)"
else
    echo "[5/5] creating AVD $AVD_NAME (device=$AVD_DEVICE)"
    [[ -n "$EXISTING_AVD" ]] && "$AVDMANAGER" delete avd -n "$AVD_NAME" >/dev/null 2>&1 || true
    # avdmanager wants a non-interactive answer to "Do you wish to create a custom hardware profile?" — pipe no.
    echo "no" | "$AVDMANAGER" create avd \
        --name "$AVD_NAME" \
        --device "$AVD_DEVICE" \
        --package "$SYSTEM_IMAGE_PKG" \
        --force
fi

# ── (6) Gradle (AGP 8.6 wants Gradle 8.7+). ──────────────────────────────

GRADLE_INSTALL="$GRADLE_DIR/gradle-$GRADLE_VERSION"
GRADLE_BIN="$GRADLE_INSTALL/bin/gradle"

if [[ -x "$GRADLE_BIN" && "$FORCE" = false ]]; then
    echo "[6/6] Gradle $GRADLE_VERSION already at $GRADLE_INSTALL (skip)"
else
    echo "[6/6] downloading Gradle $GRADLE_VERSION"
    TMP2=$(mktemp -d)
    trap 'rm -rf "$TMP2"' EXIT
    GRADLE_URL="https://services.gradle.org/distributions/gradle-${GRADLE_VERSION}-bin.zip"
    curl --fail --location --silent --show-error -o "$TMP2/gradle.zip" "$GRADLE_URL"
    rm -rf "$GRADLE_DIR"
    mkdir -p "$GRADLE_DIR"
    unzip -q "$TMP2/gradle.zip" -d "$GRADLE_DIR"
    [[ -x "$GRADLE_BIN" ]] || { echo "error: $GRADLE_BIN missing after extract" >&2; exit 1; }
fi

# ── Done. ────────────────────────────────────────────────────────────────

cat <<EOF

Android toolchain installed locally — no system PATH changes.
  SDK:    $SDK_DIR
  NDK:    $NDK_DIR  (→ $NDK_INSTALL)
  AVD:    $AVD_NAME  (Pixel 6, API 34, arm64)
  Gradle: $GRADLE_INSTALL

Env vars to use in build / test scripts (or 'export $(grep -v ^# ...)'):
  export ANDROID_HOME="$SDK_DIR"
  export ANDROID_NDK_HOME="$NDK_DIR"
  export PATH="\$ANDROID_HOME/platform-tools:\$ANDROID_HOME/emulator:$GRADLE_INSTALL/bin:\$PATH"
  export JAVA_HOME="$JAVA_HOME"
  # Keep gradle's download cache in artifacts/ so ~/.gradle stays clean.
  export GRADLE_USER_HOME="$TOOLS_DIR/gradle-user-home"

Boot the emulator (headless, ready for instrumented tests):
  \$ANDROID_HOME/emulator/emulator @$AVD_NAME -no-window -no-audio -gpu swiftshader_indirect &

Check connectivity:
  \$ANDROID_HOME/platform-tools/adb wait-for-device shell getprop sys.boot_completed
EOF
