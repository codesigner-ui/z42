#!/usr/bin/env bash
# scripts/package.sh — Build + assemble per-arch host SDK package conforming
# to docs/spec/archive/2026-05-13-define-package-layout/.
#
# Output:
#   artifacts/packages/z42-<version>-<rid>-<profile>/
#   ├── bin/                z42c + z42vm (+ z42c.pdb / z42vm.dSYM)
#   ├── libs/               *.zpkg + *.zsym + index.json (cross-package byte-identical)
#   ├── native/
#   │   ├── libz42.{a,dylib,so}  /  z42.{lib,dll}
#   │   └── include/{z42_abi,z42_host}.h
#   ├── examples/
#   │   ├── hello_c/{main.c,hello.zbc,README.md}
#   │   └── hello_rust/{Cargo.toml,src/main.rs,README.md}
#   └── manifest.toml
#
# Supported desktop RIDs (memory: project_supported_platforms):
#   macos-arm64 / linux-arm64 / linux-x64 / windows-x64
#   (macos-x64 / win-arm64 not in whitelist; iOS / Android / wasm packages
#    are produced by their respective platforms/*/build.sh.)
#
# Usage:
#   ./scripts/package.sh                                # debug, host RID
#   ./scripts/package.sh release                        # release, host RID
#   ./scripts/package.sh release --rid <rid>            # explicit RID
#   ./scripts/package.sh --help

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

source "$SCRIPT_DIR/_lib/package_helpers.sh"

# ── Arg parsing ──────────────────────────────────────────────────────────

PROFILE="debug"
RID=""
VARIANT=""

usage() {
    cat <<EOF
Usage: $(basename "$0") [release|debug] [--rid <rid>] [--variant <suffix>]

Builds + assembles a z42 host SDK distribution package.

Profile (positional, optional):
  release | debug          (default: debug)

Options:
  --rid <rid>              Target RID. One of:
                             macos-arm64 / linux-arm64 / linux-x64 / windows-x64
                           (default: auto-detected from current host)
  --variant <suffix>       Append "-<suffix>" to the package directory name.
  -h, --help               Show this help.

Cross-compile support (Phase 1):
  - Native-host only; cross-RID matrix is the CI release pipeline.

Supported platform matrix: memory project_supported_platforms.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        release|Release) PROFILE="release" ;;
        debug|Debug)     PROFILE="debug" ;;
        --rid)           RID="$2"; shift ;;
        --variant)       VARIANT="$2"; shift ;;
        -h|--help)       usage; exit 0 ;;
        *) echo "unknown arg: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

HOST_RID="$(detect_host_rid)"
RID="${RID:-$HOST_RID}"
validate_rid_supported_on_host "$RID"

CARGO_TARGET=$(rid_to_cargo "$RID")
DOTNET_RID=$(rid_to_dotnet "$RID")

VERSION=$(grep -E '^version' src/runtime/Cargo.toml | head -1 | sed -E 's/.*"([^"]+)".*/\1/')
[ -z "$VERSION" ] && VERSION="0.0.0"

PKG_NAME="z42-${VERSION}-${RID}-${PROFILE}"
[ -n "$VARIANT" ] && PKG_NAME="${PKG_NAME}-${VARIANT}"
PKG_DIR="$ROOT/artifacts/packages/$PKG_NAME"

echo "Package:        $PKG_NAME"
echo "Output:         $PKG_DIR"
echo "Target RID:     $RID  (cargo=$CARGO_TARGET, dotnet=$DOTNET_RID)"
echo "Profile:        $PROFILE"
echo "Host RID:       $HOST_RID"
echo ""

rm -rf "$PKG_DIR"
mkdir -p "$PKG_DIR/bin" "$PKG_DIR/libs" "$PKG_DIR/native/include" "$PKG_DIR/examples"

# ── 1. z42c (dotnet publish single-file) ─────────────────────────────────

echo "[1/7] z42c (dotnet publish $DOTNET_RID, $PROFILE)"
DOTNET_CONFIG="Debug"
[ "$PROFILE" = "release" ] && DOTNET_CONFIG="Release"

PUBLISH_TMP=$(mktemp -d)
trap 'rm -rf "$PUBLISH_TMP"' EXIT

dotnet publish src/compiler/z42.Driver/z42.Driver.csproj \
    -c "$DOTNET_CONFIG" -r "$DOTNET_RID" \
    -p:PublishSingleFile=true \
    -p:UseAppHost=true \
    -o "$PUBLISH_TMP" \
    --nologo -v quiet

if [ -f "$PUBLISH_TMP/z42c" ]; then
    cp "$PUBLISH_TMP/z42c" "$PKG_DIR/bin/z42c"
elif [ -f "$PUBLISH_TMP/z42c.exe" ]; then
    cp "$PUBLISH_TMP/z42c.exe" "$PKG_DIR/bin/z42c.exe"
fi
find "$PUBLISH_TMP" -maxdepth 1 -name 'z42c.pdb' -exec cp {} "$PKG_DIR/bin/" \;
echo "      ✓ bin/z42c"

# ── 2. z42vm + libz42.{a,dylib,so,dll} ───────────────────────────────────

echo "[2/7] z42vm + libz42 (cargo $CARGO_TARGET, $PROFILE)"

# (a) z42vm binary + libz42.rlib (default cargo build).
if [ "$PROFILE" = "release" ]; then
    cargo build --release --manifest-path src/runtime/Cargo.toml --target "$CARGO_TARGET" --quiet
else
    cargo build --manifest-path src/runtime/Cargo.toml --target "$CARGO_TARGET" --quiet
fi

# (b) Explicit staticlib emit; coexists with rlib in same target dir.
echo "      cargo rustc --crate-type=staticlib"
cargo rustc \
    $([ "$PROFILE" = "release" ] && echo "--release") \
    --lib --crate-type=staticlib \
    --manifest-path src/runtime/Cargo.toml \
    --target "$CARGO_TARGET" --quiet >/dev/null 2>&1 || true

# (c) Explicit cdylib emit (libz42.dylib / .so / .dll).
echo "      cargo rustc --crate-type=cdylib"
cargo rustc \
    $([ "$PROFILE" = "release" ] && echo "--release") \
    --lib --crate-type=cdylib \
    --manifest-path src/runtime/Cargo.toml \
    --target "$CARGO_TARGET" --quiet >/dev/null 2>&1 || true

CARGO_OUT="$ROOT/artifacts/build/runtime/$CARGO_TARGET/$PROFILE"

if [ -f "$CARGO_OUT/z42vm" ]; then
    cp "$CARGO_OUT/z42vm" "$PKG_DIR/bin/z42vm"
elif [ -f "$CARGO_OUT/z42vm.exe" ]; then
    cp "$CARGO_OUT/z42vm.exe" "$PKG_DIR/bin/z42vm.exe"
fi
echo "      ✓ bin/z42vm"

for f in libz42.a libz42.dylib libz42.so z42.lib z42.dll; do
    [ -f "$CARGO_OUT/$f" ] && cp "$CARGO_OUT/$f" "$PKG_DIR/native/$f"
done
echo "      ✓ native/ (libz42.* / z42.*)"

# macOS dSYM debug bundle (when produced).
[ -d "$CARGO_OUT/z42vm.dSYM" ] && cp -R "$CARGO_OUT/z42vm.dSYM" "$PKG_DIR/bin/"

# ── 3. native/include/ ───────────────────────────────────────────────────

echo "[3/7] C ABI headers"
pkg_copy_native_includes "$PKG_DIR"
echo "      ✓ native/include/ ($(ls "$PKG_DIR/native/include/" | wc -l | tr -d ' ') files)"

# ── 4. stdlib zpkg + zsym + index.json ───────────────────────────────────

echo "[4/7] stdlib zpkg + zsym + index.json"
pkg_copy_libs "$PKG_DIR"
zpkg_count=$(ls "$PKG_DIR/libs/"*.zpkg 2>/dev/null | wc -l | tr -d ' ')
echo "      ✓ libs/ ($zpkg_count zpkg + index.json)"

# ── 5. examples/hello_c ──────────────────────────────────────────────────

echo "[5/7] examples/hello_c"
pkg_emit_examples_hello_c "$PKG_DIR" \
    "$ROOT/examples/embedding/hello_c/README.md.host"
echo "      ✓ examples/hello_c/"

# ── 6. examples/hello_rust (desktop only) ────────────────────────────────

echo "[6/7] examples/hello_rust"
pkg_emit_examples_hello_rust "$PKG_DIR"
echo "      ✓ examples/hello_rust/"

# ── 7. manifest.toml ─────────────────────────────────────────────────────

echo "[7/7] manifest.toml"
pkg_emit_manifest "$PKG_DIR" "$RID" "$VERSION" "$PROFILE" "$HOST_RID"
echo "      ✓ manifest.toml"

# ── SHA-256 invariant check (R3 / R4 / R7) ───────────────────────────────

echo ""
echo "SHA-256 invariant check:"
pkg_sha256_check "$PKG_DIR" || exit 1

echo ""
echo "✅ Package assembled: $PKG_DIR/"
echo ""
echo "Try it:"
echo "  cd $PKG_DIR/examples/hello_c && \\"
case "$RID" in
    macos-*) echo "    cc -I ../../native/include -o hello_c main.c \\";
             echo "       -L ../../native -lz42 -liconv -lSystem -lc -lm && \\";;
    linux-*) echo "    cc -I ../../native/include -o hello_c main.c \\";
             echo "       -L ../../native -lz42 -lc -lm -lpthread -ldl -lrt -lgcc_s && \\";;
    windows-*) echo "    cl /I..\\..\\native\\include main.c /link ..\\..\\native\\z42.lib && \\";;
esac
echo "    ./hello_c hello.zbc ../../libs/"
