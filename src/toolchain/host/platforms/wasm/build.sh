#!/usr/bin/env bash
# Build `@z42/wasm` end-to-end:
#   1. Verify required tooling.
#   2. Compile test fixtures from examples/embedding/*.z42 → js/fixtures/.
#   3. Copy stdlib zpkgs + index.json from artifacts/build/libraries/dist/release/
#      into js/stdlib/.
#   4. Run wasm-pack for web + nodejs targets.
#
# Spec: docs/spec/archive/2026-05-12-add-platform-wasm/
#       docs/spec/archive/2026-05-12-add-wasm-tests/  (stale-path fix + fixtures)

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../../../.." && pwd)"

# 单一真相源：repo 根 versions.toml。scripts/_lib/versions.sh 已随 scripts→xtask
# 迁移删除；这里内联一个最小 tomllib 读取器（python3 本就是构建前置）。值仍单一
# 源自 versions.toml。版本下限改为 presence-only 守卫（原 compare 仅 warning）。
command -v python3 >/dev/null 2>&1 || { echo "error: python3 required to read versions.toml" >&2; exit 1; }
command -v rustc   >/dev/null 2>&1 || echo "warning: rustc not found (https://rustup.rs)" >&2
command -v dotnet  >/dev/null 2>&1 || echo "warning: dotnet not found (.NET 8+)" >&2
command -v node    >/dev/null 2>&1 || echo "warning: node not found (versions.toml [toolchain.node])" >&2
_vget()  { python3 -c "import tomllib;d=tomllib.load(open('$ROOT/versions.toml','rb'))
for p in '$1'.split('.'): d=d[p]
print(d)"; }
_vlist() { python3 -c "import tomllib;d=tomllib.load(open('$ROOT/versions.toml','rb'))
for p in '$1'.split('.'): d=d[p]
print(' '.join(map(str,d)))"; }

WASM_RUST_TARGETS=$(_vlist platform.wasm.rust_targets)
WASM_PACK_MIN=$(_vget build.wasm.wasm_pack_min)
NODE_MIN=$(_vget toolchain.node.min_version)

# ── (1) Tooling check (fail-fast — we do not auto-install). ──────────────

if ! command -v wasm-pack >/dev/null 2>&1; then
    echo "error: wasm-pack not found on PATH (z42 requires $WASM_PACK_MIN+; versions.toml [build.wasm])." >&2
    echo "       install via: cargo install wasm-pack --locked" >&2
    exit 1
fi

for t in $WASM_RUST_TARGETS; do
    if ! rustup target list --installed | grep -q "^$t$"; then
        echo "error: rustc target $t not installed (declared in versions.toml [platform.wasm].rust_targets)." >&2
        echo "       install via: rustup target add $t" >&2
        exit 1
    fi
done

# dotnet/node 已在头部 presence 守卫校过

DRIVER_DLL="$ROOT/artifacts/build/compiler/z42.Driver/bin/z42c.dll"
if [[ ! -f "$DRIVER_DLL" ]]; then
    echo "error: z42c driver not found at $DRIVER_DLL" >&2
    echo "       build the compiler first: dotnet build $ROOT/src/compiler/z42.slnx" >&2
    exit 1
fi

# ── (2) Compile fixtures from examples/embedding/*.z42 → js/fixtures/. ───
#
# Fixtures are shared with add-ios-tests and any future platform spec;
# they live under `examples/embedding/` so all platforms reuse one
# source-of-truth.

FIX_DIR="$HERE/js/fixtures"
mkdir -p "$FIX_DIR"
for src in hello multi_line; do
    src_file="$ROOT/examples/embedding/${src}.z42"
    out_file="$FIX_DIR/${src}.zbc"
    if [[ ! -f "$src_file" ]]; then
        echo "error: fixture source missing: $src_file" >&2
        exit 1
    fi
    echo "z42c $src.z42 → $out_file"
    dotnet "$DRIVER_DLL" "$src_file" --emit zbc -o "$out_file"
done

# ── (3) Stdlib bundle (zpkgs + namespace index). ─────────────────────────

LIBS_DIR="$ROOT/artifacts/build/libraries/dist/release"
STDLIB_DIR="$HERE/js/stdlib"

if [[ -d "$LIBS_DIR" ]]; then
    echo "copying stdlib zpkgs + index from $LIBS_DIR"
    mkdir -p "$STDLIB_DIR"
    cp "$LIBS_DIR"/*.zpkg "$STDLIB_DIR/" 2>/dev/null || true
    ls "$STDLIB_DIR"/*.zpkg 2>/dev/null | xargs -n1 basename | sed 's/^/  - /' || true
    if [[ -f "$LIBS_DIR/index.json" ]]; then
        cp "$LIBS_DIR/index.json" "$STDLIB_DIR/index.json"
        echo "  - index.json"
    else
        echo "warning: $LIBS_DIR/index.json missing — bundleStdlib* will fall back to namespace-as-filename" >&2
    fi
else
    echo "warning: stdlib libs dir not found at $LIBS_DIR" >&2
    echo "         build the standard library first: ./scripts/build-stdlib.sh" >&2
fi

# ── (4) wasm-pack: produce pkg-web/ + pkg-nodejs/. ──────────────────────

cd "$HERE"

echo "wasm-pack build --target web"
wasm-pack build --target web --out-dir pkg-web --out-name z42_wasm --no-typescript

echo "wasm-pack build --target nodejs"
wasm-pack build --target nodejs --out-dir pkg-nodejs --out-name z42_wasm --no-typescript

echo ""
echo "built:"
echo "  $HERE/pkg-web/"
echo "  $HERE/pkg-nodejs/"
echo "  $STDLIB_DIR/                         (zpkgs + index.json)"
echo "  $FIX_DIR/                            (test fixtures)"
echo ""
echo "run tests:  ./test.sh   (Node.js via artifacts/tools/node — run ./scripts/install-node-local.sh first)"
echo "run demo:   $ROOT/artifacts/tools/node/bin/node demo/node/run.js"
