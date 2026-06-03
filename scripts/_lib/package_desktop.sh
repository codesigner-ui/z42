#!/usr/bin/env bash
# package_desktop.sh — Desktop SDK package pipeline (host RIDs).
#
# Args: <pkg_dir> <rid> <version> <profile> <host_rid>
# Called from scripts/package.sh after dispatch on RID category = desktop.

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
DOTNET_RID=$(rid_to_dotnet "$RID")

DOTNET_CONFIG="Debug"
[ "$PROFILE" = "release" ] && DOTNET_CONFIG="Release"

# ── 1. z42c (dotnet publish single-file) ─────────────────────────────────

echo "[1/7] z42c (dotnet publish $DOTNET_RID, $PROFILE)"
PUBLISH_TMP=$(mktemp -d)
trap 'rm -rf "$PUBLISH_TMP"' EXIT

dotnet publish "$ROOT/src/compiler/z42.Driver/z42.Driver.csproj" \
    -c "$DOTNET_CONFIG" -r "$DOTNET_RID" \
    -p:PublishSingleFile=true \
    -p:UseAppHost=true \
    -o "$PUBLISH_TMP" \
    --nologo -v quiet

if   [ -f "$PUBLISH_TMP/z42c" ];     then cp "$PUBLISH_TMP/z42c"     "$PKG_DIR/bin/z42c"
elif [ -f "$PUBLISH_TMP/z42c.exe" ]; then cp "$PUBLISH_TMP/z42c.exe" "$PKG_DIR/bin/z42c.exe"; fi
find "$PUBLISH_TMP" -maxdepth 1 -name 'z42c.pdb' -exec cp {} "$PKG_DIR/bin/" \;
echo "      ✓ bin/z42c"

# ── 2. z42vm + libz42.{a,dylib,so,dll} ───────────────────────────────────

echo "[2/7] z42vm + libz42 (cargo $CARGO_TARGET, $PROFILE)"

if [ "$PROFILE" = "release" ]; then
    cargo build --release --manifest-path "$ROOT/src/runtime/Cargo.toml" --target "$CARGO_TARGET" --quiet
else
    cargo build           --manifest-path "$ROOT/src/runtime/Cargo.toml" --target "$CARGO_TARGET" --quiet
fi

# Explicit staticlib + cdylib emit (rlib already from `cargo build`).
echo "      cargo rustc --crate-type=staticlib"
cargo rustc \
    $([ "$PROFILE" = "release" ] && echo "--release") \
    --lib --crate-type=staticlib \
    --manifest-path "$ROOT/src/runtime/Cargo.toml" \
    --target "$CARGO_TARGET" --quiet >/dev/null 2>&1 || true

echo "      cargo rustc --crate-type=cdylib"
cargo rustc \
    $([ "$PROFILE" = "release" ] && echo "--release") \
    --lib --crate-type=cdylib \
    --manifest-path "$ROOT/src/runtime/Cargo.toml" \
    --target "$CARGO_TARGET" --quiet >/dev/null 2>&1 || true

CARGO_OUT="$ROOT/artifacts/build/runtime/$CARGO_TARGET/$PROFILE"

if   [ -f "$CARGO_OUT/z42vm" ];     then cp "$CARGO_OUT/z42vm"     "$PKG_DIR/bin/z42vm"
elif [ -f "$CARGO_OUT/z42vm.exe" ]; then cp "$CARGO_OUT/z42vm.exe" "$PKG_DIR/bin/z42vm.exe"; fi
echo "      ✓ bin/z42vm"

for f in libz42.a libz42.dylib libz42.so z42.lib z42.dll; do
    [ -f "$CARGO_OUT/$f" ] && cp "$CARGO_OUT/$f" "$PKG_DIR/native/$f"
done
echo "      ✓ native/ (libz42.* / z42.*)"

[ -d "$CARGO_OUT/z42vm.dSYM" ] && cp -R "$CARGO_OUT/z42vm.dSYM" "$PKG_DIR/bin/"

# ── 2b. z42-compression cdylib (add-z42-compression, 2026-05-22) ─────────
# Separate Cargo workspace member; z42vm dlopens it at startup from
# <pkg_dir>/native/ via the default search path in
# `src/runtime/src/native/ext.rs`. Produces both .{so,dylib,dll} (for
# dlopen) and .a (for mobile integrators who prefer compile-time link).

echo "[2b/7] z42-compression cdylib + staticlib (cargo $CARGO_TARGET, $PROFILE)"

cargo build \
    $([ "$PROFILE" = "release" ] && echo "--release") \
    -p z42-compression \
    --manifest-path "$ROOT/src/runtime/Cargo.toml" \
    --target "$CARGO_TARGET" --quiet

for f in libz42_compression.a libz42_compression.dylib libz42_compression.so \
         z42_compression.dll z42_compression.lib; do
    [ -f "$CARGO_OUT/$f" ] && cp "$CARGO_OUT/$f" "$PKG_DIR/native/$f"
done
echo "      ✓ native/libz42_compression.* (dlopened by z42vm at startup)"

# ── 2c. z42 launcher: trampoline (bin/z42) + launcher.zpkg (pkg root) ─────
# bundle-launcher-in-release (2026-06-03): the trampoline is target-specific
# (cargo --target); launcher.zpkg is RID-independent bytecode (built with the
# host driver, so cross-packaging works). At runtime the trampoline resolves
# its runtime package-relative (bin/z42vm + ../launcher.zpkg + ../libs) so
# `<pkg>/bin/z42 run app.zpkg` works unpacked, no install.

echo "[2c/7] z42 launcher (trampoline + launcher.zpkg)"

cargo build \
    $([ "$PROFILE" = "release" ] && echo "--release") \
    --manifest-path "$ROOT/src/toolchain/launcher/Cargo.toml" \
    --target "$CARGO_TARGET" --quiet

if   [ -f "$CARGO_OUT/z42" ];     then cp "$CARGO_OUT/z42"     "$PKG_DIR/bin/z42"
elif [ -f "$CARGO_OUT/z42.exe" ]; then cp "$CARGO_OUT/z42.exe" "$PKG_DIR/bin/z42.exe"; fi
echo "      ✓ bin/z42 (trampoline)"

# launcher core → launcher.zpkg. Built with the host driver (RID-independent
# bytecode) + the dev stdlib flat view (matching this repo's version).
( cd "$ROOT" && Z42_LIBS="$ROOT/artifacts/build/libs/release" \
    dotnet run --project src/compiler/z42.Driver --verbosity quiet -- \
    build src/toolchain/launcher/core/z42.launcher.z42.toml --release ) >/dev/null
cp "$ROOT/src/toolchain/launcher/core/dist/z42.launcher.zpkg" "$PKG_DIR/launcher.zpkg"
echo "      ✓ launcher.zpkg"

# install-z42-to-home: ship the installer so `./install.sh` sets up $Z42_HOME.
cp "$ROOT/scripts/install.sh" "$PKG_DIR/install.sh"
chmod +x "$PKG_DIR/install.sh"
echo "      ✓ install.sh"

# ── 3-7. Headers / libs / examples / manifest ───────────────────────────

echo "[3/7] C ABI headers"
pkg_copy_native_includes "$PKG_DIR"

echo "[4/7] stdlib zpkg + zsym + index.json"
pkg_copy_libs "$PKG_DIR"

echo "[5/7] examples/hello_c"
pkg_emit_examples_hello_c "$PKG_DIR" "$ROOT/examples/embedding/hello_c/README.md.host"

echo "[6/7] examples/hello_rust"
pkg_emit_examples_hello_rust "$PKG_DIR"

echo "[7/7] manifest.toml"
pkg_emit_manifest "$PKG_DIR" "$RID" "$VERSION" "$PROFILE" "$HOST_RID"
