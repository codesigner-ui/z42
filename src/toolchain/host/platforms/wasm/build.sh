#!/usr/bin/env bash
# Build `@z42/wasm` end-to-end:
#   1. Verify required tooling.
#   2. Compile the demo .z42 fixture to .zbc via z42c.dll.
#   3. Copy stdlib zpkgs from artifacts/z42/libs/ into js/stdlib/.
#   4. Run wasm-pack for web + nodejs targets.
#
# Spec: docs/spec/archive/2026-05-12-add-platform-wasm/

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../../../.." && pwd)"

# ── (1) Tooling check (fail-fast — we do not auto-install). ──────────────

if ! command -v wasm-pack >/dev/null 2>&1; then
    echo "error: wasm-pack not found on PATH." >&2
    echo "       install via: cargo install wasm-pack --locked" >&2
    exit 1
fi

if ! rustup target list --installed | grep -q '^wasm32-unknown-unknown$'; then
    echo "error: rustc target wasm32-unknown-unknown not installed." >&2
    echo "       install via: rustup target add wasm32-unknown-unknown" >&2
    exit 1
fi

# ── (2) Compile the demo fixture if z42c is available. ───────────────────

Z42C="$ROOT/artifacts/compiler/z42.Driver/bin/z42c.dll"
SRC="$HERE/demo/fixtures/hello.z42"
ZBC="$HERE/demo/fixtures/hello.zbc"

if [[ -f "$Z42C" ]]; then
    echo "compiling fixture: $(basename "$SRC")"
    dotnet "$Z42C" "$SRC" --emit zbc -o "$ZBC"
else
    echo "warning: z42c not built — run 'dotnet build src/compiler/z42.slnx' first" >&2
    echo "         skipping fixture compile; node demo will not run end-to-end" >&2
fi

# ── (3) Stdlib bundle: copy artifacts/z42/libs/*.zpkg → js/stdlib/. ──────

LIBS_DIR="$ROOT/artifacts/z42/libs"
STDLIB_DIR="$HERE/js/stdlib"

if [[ -d "$LIBS_DIR" ]]; then
    echo "copying stdlib zpkgs from $LIBS_DIR"
    mkdir -p "$STDLIB_DIR"
    cp "$LIBS_DIR"/*.zpkg "$STDLIB_DIR/" 2>/dev/null || true
    ls "$STDLIB_DIR"/*.zpkg 2>/dev/null | xargs -n1 basename | sed 's/^/  - /' || true
else
    echo "warning: stdlib libs dir not found at $LIBS_DIR" >&2
    echo "         build the standard library first: dotnet build src/compiler/z42.slnx" >&2
fi

# ── (4) wasm-pack: produce pkg-web/ + pkg-nodejs/. ──────────────────────

cd "$HERE"

# wasm-pack needs a target dir under the crate. We keep both builds
# parallel-dir so consumers can pick by `import` map.
echo "wasm-pack build --target web"
wasm-pack build --target web --out-dir pkg-web --out-name z42_wasm --no-typescript

echo "wasm-pack build --target nodejs"
wasm-pack build --target nodejs --out-dir pkg-nodejs --out-name z42_wasm --no-typescript

echo ""
echo "built:"
echo "  $HERE/pkg-web/"
echo "  $HERE/pkg-nodejs/"
echo "  $STDLIB_DIR/"
echo ""
echo "run the node demo with:  node $HERE/demo/node/run.js"
