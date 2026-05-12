#!/usr/bin/env bash
# scripts/package.sh — Build + assemble distribution package.
#
# Spec: docs/spec/archive/2026-05-12-redesign-artifact-layout/
#
# Output layout:
#   artifacts/packages/z42-<version>-<rid>-<config>[-<variant>]/
#   ├── bin/                          # z42c, z42vm (+ .pdb / .dSYM)
#   ├── libs/                         # *.zpkg + *.zsym
#   └── native/
#       ├── libz42.{dylib,so,a} / z42.{dll,lib}
#       └── include/                  # z42_host.h, z42_abi.h
#
# Usage:
#   ./scripts/package.sh                    # debug build
#   ./scripts/package.sh release            # release build
#   ./scripts/package.sh release --variant mt  # custom variant suffix

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

PROFILE="debug"
VARIANT=""
while [ $# -gt 0 ]; do
    case "$1" in
        release|Release) PROFILE="release" ;;
        debug|Debug)     PROFILE="debug" ;;
        --variant)       VARIANT="$2"; shift ;;
        *) echo "unknown arg: $1" >&2; exit 2 ;;
    esac
    shift
done

# 大小写：build dir 用小写 (release/debug)；packages name 也用小写。
RUNTIME_MANIFEST="src/runtime/Cargo.toml"
COMPILER_PROJECT="src/compiler/z42.Driver/z42.Driver.csproj"
STDLIB_MODULES=(z42.core z42.io z42.math z42.text z42.collections z42.test)

# ── 1. Detect RID ─────────────────────────────────────────────────────────────
detect_rid() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"
    case "$os" in
        Darwin)
            case "$arch" in
                arm64) echo "osx-arm64" ;;
                *)     echo "osx-x64" ;;
            esac ;;
        Linux)
            case "$arch" in
                aarch64) echo "linux-arm64" ;;
                *)       echo "linux-x64" ;;
            esac ;;
        MINGW*|MSYS*|CYGWIN*) echo "win-x64" ;;
        *) echo "win-x64" ;;
    esac
}

RID=$(detect_rid)

# ── 2. Read version ──────────────────────────────────────────────────────────
VERSION=$(grep -E '^version' src/runtime/Cargo.toml | head -1 | sed -E 's/.*"([^"]+)".*/\1/')
[ -z "$VERSION" ] && VERSION="0.0.0"

PKG_NAME="z42-${VERSION}-${RID}-${PROFILE}"
[ -n "$VARIANT" ] && PKG_NAME="${PKG_NAME}-${VARIANT}"
PKG_DIR="$ROOT/artifacts/packages/$PKG_NAME"

echo "Package: $PKG_NAME"
echo "Output:  $PKG_DIR"
echo ""

rm -rf "$PKG_DIR"
mkdir -p "$PKG_DIR/bin" "$PKG_DIR/libs" "$PKG_DIR/native/include"

# ── 3. Build + copy z42c ──────────────────────────────────────────────────────
echo "[1/5] z42c (dotnet publish single-file, $RID, $PROFILE)"
DOTNET_CONFIG="Debug"
[ "$PROFILE" = "release" ] && DOTNET_CONFIG="Release"

PUBLISH_TMP=$(mktemp -d)
trap 'rm -rf "$PUBLISH_TMP"' EXIT

# UseAppHost=true overrides Directory.Build.props (which sets false for dev
# builds to avoid apphost DOTNET_ROOT resolution); single-file publish needs
# the apphost executable.
dotnet publish "$COMPILER_PROJECT" \
    -c "$DOTNET_CONFIG" -r "$RID" \
    -p:PublishSingleFile=true \
    -p:UseAppHost=true \
    -o "$PUBLISH_TMP" \
    --nologo -v quiet

# z42c binary 名跨平台：macOS / Linux 是 z42c，Windows 是 z42c.exe
if [ -f "$PUBLISH_TMP/z42c" ]; then
    cp "$PUBLISH_TMP/z42c" "$PKG_DIR/bin/z42c"
elif [ -f "$PUBLISH_TMP/z42c.exe" ]; then
    cp "$PUBLISH_TMP/z42c.exe" "$PKG_DIR/bin/z42c.exe"
fi

# 拷 dotnet symbol 文件（.pdb）
find "$PUBLISH_TMP" -maxdepth 1 -name 'z42c.pdb' -exec cp {} "$PKG_DIR/bin/" \;

echo "      ✓ $PKG_DIR/bin/z42c"

# ── 4. Build + copy z42vm + native libs ───────────────────────────────────────
echo "[2/5] z42vm + libz42 (cargo build, $PROFILE)"
if [ "$PROFILE" = "release" ]; then
    cargo build --release --manifest-path "$RUNTIME_MANIFEST"
    CARGO_OUT="artifacts/build/runtime/release"
else
    cargo build --manifest-path "$RUNTIME_MANIFEST"
    CARGO_OUT="artifacts/build/runtime/debug"
fi

# z42vm binary
if [ -f "$CARGO_OUT/z42vm" ]; then
    cp "$CARGO_OUT/z42vm" "$PKG_DIR/bin/z42vm"
elif [ -f "$CARGO_OUT/z42vm.exe" ]; then
    cp "$CARGO_OUT/z42vm.exe" "$PKG_DIR/bin/z42vm.exe"
fi
echo "      ✓ $PKG_DIR/bin/z42vm"

# native libs（lib + import lib + static lib）
for f in libz42.dylib libz42.so libz42.a z42.dll z42.lib; do
    [ -f "$CARGO_OUT/$f" ] && cp "$CARGO_OUT/$f" "$PKG_DIR/native/$f"
done
echo "      ✓ $PKG_DIR/native/  (libz42.* / z42.*)"

# macOS dSYM debug bundle（如果 cargo release 配置了）
[ -d "$CARGO_OUT/z42vm.dSYM" ] && cp -R "$CARGO_OUT/z42vm.dSYM" "$PKG_DIR/bin/"

# ── 5. Headers ─────────────────────────────────────────────────────────────────
echo "[3/5] C headers"
cp src/runtime/include/*.h "$PKG_DIR/native/include/" 2>/dev/null || true
echo "      ✓ $PKG_DIR/native/include/  ($(ls "$PKG_DIR/native/include/" | wc -l | tr -d ' ') files)"

# ── 6. Stdlib zpkg + zsym ─────────────────────────────────────────────────────
echo "[4/5] stdlib zpkg + zsym"
SRC_LIBS="$ROOT/artifacts/build/libraries"
copied=0
for mod in "${STDLIB_MODULES[@]}"; do
    zpkg="$SRC_LIBS/$mod/$PROFILE/dist/$mod.zpkg"
    zsym="$SRC_LIBS/$mod/$PROFILE/dist/$mod.zsym"
    if [ -f "$zpkg" ]; then
        cp "$zpkg" "$PKG_DIR/libs/$mod.zpkg"
        [ -f "$zsym" ] && cp "$zsym" "$PKG_DIR/libs/$mod.zsym"
        ((copied++)) || true
    else
        echo "      ⚠ $mod.zpkg missing — run ./scripts/build-stdlib.sh first"
    fi
done
echo "      ✓ $PKG_DIR/libs/  ($copied/${#STDLIB_MODULES[@]} stdlib packages)"

# ── 7. manifest.toml ───────────────────────────────────────────────────────────
echo "[5/5] manifest.toml"
cat > "$PKG_DIR/manifest.toml" <<EOF
[package]
version = "$VERSION"
rid     = "$RID"
config  = "$PROFILE"
variant = "$VARIANT"
created = "$(date -u +%Y-%m-%dT%H:%M:%SZ)"

[layout]
bin     = "bin/"
libs    = "libs/"
native  = "native/"
include = "native/include/"
EOF
echo "      ✓ $PKG_DIR/manifest.toml"

echo ""
echo "Done. Package assembled at:"
echo "  $PKG_DIR/"
echo ""
echo "Test with:"
echo "  ./scripts/test-dist.sh"
