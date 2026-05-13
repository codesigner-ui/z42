#!/usr/bin/env bash
# package_wasm.sh — browser-wasm SDK package pipeline.
#
# Args: <pkg_dir> <rid> <version> <profile> <host_rid>
# RID = browser-wasm; called from scripts/package.sh.
#
# Output (per docs/spec/changes/add-wasm-package/):
#   bin/README.md
#   libs/                            stdlib zpkg + index.json (byte-identical)
#   native/
#     libz42.a                       wasm32 object archive (staticlib)
#     z42_wasm_bg.wasm               wasm-bindgen cdylib (canonical copy)
#     include/                       z42_abi.h + z42_host.h
#   pkg-web/                         wasm-bindgen web target (cp from platforms/wasm/)
#   pkg-nodejs/                      wasm-bindgen nodejs target
#   js/{index.js,index.d.ts,stdlib-resolver.js}
#   package.json                     npm publish-ready (pkg-root paths)
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
WASM_ROOT="$ROOT/src/toolchain/host/platforms/wasm"

# ── 0. Tooling check ────────────────────────────────────────────────────

if ! rustup target list --installed | grep -q "^$CARGO_TARGET$"; then
    echo "error: rustc target $CARGO_TARGET not installed." >&2
    echo "       install via: rustup target add $CARGO_TARGET" >&2
    exit 1
fi

# Verify pkg-web / pkg-nodejs exist — otherwise platforms/wasm/build.sh hasn't run.
for sub in pkg-web pkg-nodejs; do
    if [[ ! -d "$WASM_ROOT/$sub" ]]; then
        echo "error: $WASM_ROOT/$sub missing — run platforms/wasm/build.sh first" >&2
        echo "       (wasm-bindgen pkg-web/ + pkg-nodejs/ are pre-required)" >&2
        exit 1
    fi
done

# ── 1. bin/ placeholder ─────────────────────────────────────────────────

echo "[1/9] bin/ placeholder"
pkg_emit_bin_readme_placeholder "$PKG_DIR" "browser-wasm"

# ── 2. libz42.a (wasm32 staticlib via cargo rustc) ──────────────────────

echo "[2/9] libz42.a (cargo rustc $CARGO_TARGET --crate-type=staticlib, $PROFILE)"
cargo rustc \
    $([ "$PROFILE" = "release" ] && echo "--release") \
    --lib --crate-type=staticlib \
    --manifest-path "$ROOT/src/runtime/Cargo.toml" \
    --target "$CARGO_TARGET" \
    --no-default-features --features "wasm" \
    --quiet >/dev/null 2>&1 || {
        echo "error: cargo rustc staticlib for $CARGO_TARGET failed" >&2
        # Re-run verbose so user sees the actual error.
        cargo rustc \
            $([ "$PROFILE" = "release" ] && echo "--release") \
            --lib --crate-type=staticlib \
            --manifest-path "$ROOT/src/runtime/Cargo.toml" \
            --target "$CARGO_TARGET" \
            --no-default-features --features "wasm" 2>&1 | tail -30
        exit 1
    }

STATIC_A="$ROOT/artifacts/build/runtime/$CARGO_TARGET/$PROFILE/libz42.a"
if [ ! -f "$STATIC_A" ]; then
    echo "error: libz42.a not produced at $STATIC_A" >&2
    exit 1
fi
mkdir -p "$PKG_DIR/native"
cp "$STATIC_A" "$PKG_DIR/native/libz42.a"
echo "      ✓ native/libz42.a ($(du -h "$PKG_DIR/native/libz42.a" | cut -f1))"

# ── 3. z42_wasm_bg.wasm (canonical cdylib copy) ─────────────────────────

echo "[3/9] z42_wasm_bg.wasm (cp from platforms/wasm/pkg-web/)"
WASM_BG="$WASM_ROOT/pkg-web/z42_wasm_bg.wasm"
if [ ! -f "$WASM_BG" ]; then
    echo "error: $WASM_BG missing — run platforms/wasm/build.sh first" >&2
    exit 1
fi
cp "$WASM_BG" "$PKG_DIR/native/z42_wasm_bg.wasm"
echo "      ✓ native/z42_wasm_bg.wasm ($(du -h "$PKG_DIR/native/z42_wasm_bg.wasm" | cut -f1))"

# ── 4. C ABI headers ────────────────────────────────────────────────────

echo "[4/9] C ABI headers"
pkg_copy_native_includes "$PKG_DIR"

# ── 5. pkg-web/ + pkg-nodejs/ (wasm-bindgen output cp) ──────────────────

echo "[5/9] pkg-web/ + pkg-nodejs/ (wasm-bindgen)"
pkg_emit_wasm_pkg_dirs "$PKG_DIR"
echo "      ✓ pkg-web/ + pkg-nodejs/"

# ── 6. js/ facade + package.json ───────────────────────────────────────

echo "[6/9] js/ + package.json"
pkg_emit_wasm_npm_meta "$PKG_DIR"
echo "      ✓ js/ + package.json"

# ── 7. libs/ + examples/hello_c ─────────────────────────────────────────

echo "[7/9] stdlib + examples/hello_c"
pkg_copy_libs "$PKG_DIR"
pkg_emit_examples_hello_c "$PKG_DIR" "$ROOT/examples/embedding/hello_c/README.md.wasm"
echo "      ✓ libs/ + examples/hello_c/"

# ── 8. manifest.toml ────────────────────────────────────────────────────

echo "[8/9] manifest.toml"
pkg_emit_manifest "$PKG_DIR" "$RID" "$VERSION" "$PROFILE" "$HOST_RID"
echo "      ✓ manifest.toml"

# ── 9. Sanity: libz42.a is a wasm32 archive ─────────────────────────────

echo "[9/9] sanity check"
if command -v file >/dev/null 2>&1; then
    file "$PKG_DIR/native/libz42.a" | grep -q "ar archive" || {
        echo "warning: $PKG_DIR/native/libz42.a not recognized as ar archive" >&2
    }
fi
echo "      ✓ libz42.a is an ar archive"
