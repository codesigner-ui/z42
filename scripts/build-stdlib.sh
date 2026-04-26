#!/usr/bin/env bash
# build-stdlib.sh — compile z42 standard library packages via workspace mode.
#
# Workspace 配置：src/libraries/z42.workspace.toml
#   - 集中产物到 artifacts/libraries/<lib>.zpkg
#   - 集中 cache 到 artifacts/libraries/.cache/<lib>/
#   - 拓扑顺序自动（z42.core 先编，其余依赖它的后编）
#
# 同时复制到 artifacts/z42/libs/ 维持现有 VM 加载路径与分发产物兼容
# （artifacts/z42/libs 用于 package.sh 等下游消费；artifacts/libraries
# 是 workspace 直接产物）。
#
# Usage:
#   ./scripts/build-stdlib.sh                 # release build, uses dotnet run
#   ./scripts/build-stdlib.sh --use-dist      # uses packaged z42c from artifacts/z42/bin/
#
# Output:
#   artifacts/libraries/<lib>.zpkg     (workspace 直接产物)
#   artifacts/z42/libs/<lib>.zpkg      (复制以维持 VM 加载约定)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
WS_DIR="$ROOT/src/libraries"
DIST_DIR="$ROOT/artifacts/libraries"          # workspace 集中产物
LEGACY_DIR="$ROOT/artifacts/z42/libs"         # VM / 分发版加载路径

USE_DIST=false
for arg in "$@"; do
    if [ "$arg" = "--use-dist" ]; then
        USE_DIST=true
    fi
done

mkdir -p "$LEGACY_DIR"

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

LIBS=(z42.core z42.io z42.math z42.text z42.collections)

# Workspace 模式：cd 到 src/libraries 触发 workspace 发现；
# z42c build --workspace --release 编译所有 default-members
echo "  building stdlib workspace (release, all members)"
( cd "$WS_DIR" && "${COMPILER_CMD[@]}" build --workspace --release )

# 把 artifacts/libraries/<lib>.zpkg 复制到 artifacts/z42/libs（兼容现有 VM）
ok=0
fail=0
for lib in "${LIBS[@]}"; do
    src="$DIST_DIR/$lib.zpkg"
    dst="$LEGACY_DIR/$lib.zpkg"
    if [[ -f "$src" && -s "$src" ]]; then
        cp "$src" "$dst"
        size=$(wc -c < "$dst" | tr -d ' ')
        echo "    ✓ $lib.zpkg ($size bytes) → $dst"
        ((ok++)) || true
    else
        echo "    ✗ $lib.zpkg — workspace product missing at $src"
        ((fail++)) || true
    fi
done

echo ""
echo "build-stdlib: $ok succeeded, $fail failed"
if [[ $fail -gt 0 ]]; then
    exit 1
fi
