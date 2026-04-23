#!/usr/bin/env bash
# build-stdlib.sh — compile z42 standard library packages to artifacts/z42/libs/
#
# Usage:
#   ./scripts/build-stdlib.sh                 # debug build, uses dotnet run
#   ./scripts/build-stdlib.sh release         # release build, uses dotnet run
#   ./scripts/build-stdlib.sh --use-dist      # uses packaged z42c from artifacts/z42/bin/
#
# Output: artifacts/z42/libs/<lib>.zpkg (non-zero size, compiled from source)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
OUT_DIR="$ROOT/artifacts/z42/libs"
# stdlib distribution always uses packed mode (--release) so the copied .zpkg
# is self-contained and not dependent on relative .zbc paths.
RELEASE_ARG="--release"

USE_DIST=false
for arg in "$@"; do
    if [ "$arg" = "--use-dist" ]; then
        USE_DIST=true
    fi
done

mkdir -p "$OUT_DIR"

LIBS=(z42.core z42.io z42.math z42.numerics z42.text z42.collections)

# Determine compiler invocation
if [ "$USE_DIST" = true ]; then
    Z42C="$ROOT/artifacts/z42/bin/z42c"
    if [ ! -x "$Z42C" ]; then
        echo "error: z42c not found at $Z42C"
        echo "       Run: ./scripts/package.sh"
        exit 1
    fi
    echo "Using packaged compiler: $Z42C"
    COMPILER_CMD=("$Z42C")
else
    echo "Using dotnet run compiler"
    COMPILER_CMD=(dotnet run --project "$ROOT/src/compiler/z42.Driver" --)
fi

ok=0
fail=0

for lib in "${LIBS[@]}"; do
    toml="$ROOT/src/libraries/$lib/$lib.z42.toml"
    if [[ ! -f "$toml" ]]; then
        echo "  [skip] $lib — manifest not found: $toml"
        continue
    fi
    dist="$ROOT/src/libraries/$lib/dist/$lib.zpkg"
    out="$OUT_DIR/$lib.zpkg"
    echo "  building $lib"
    if "${COMPILER_CMD[@]}" build "$toml" $RELEASE_ARG 2>&1; then
        if [[ -f "$dist" && -s "$dist" ]]; then
            cp "$dist" "$out"
            size=$(wc -c < "$out" | tr -d ' ')
            echo "    ✓ $lib.zpkg ($size bytes) → $out"
            ((ok++)) || true
        else
            echo "    ✗ $lib.zpkg — dist file missing or empty"
            ((fail++)) || true
        fi
    else
        echo "    ✗ $lib — build failed"
        ((fail++)) || true
    fi
done

echo ""
echo "build-stdlib: $ok succeeded, $fail failed"
if [[ $fail -gt 0 ]]; then
    exit 1
fi
