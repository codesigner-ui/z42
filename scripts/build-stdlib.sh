#!/usr/bin/env bash
# build-stdlib.sh — compile z42 standard library packages via workspace mode.
#
# Workspace 配置：src/libraries/z42.workspace.toml
#   - 每个 member 子目录布局：artifacts/libraries/<lib>/dist/<lib>.zpkg
#                             + artifacts/libraries/<lib>/cache/<file>.zbc
#   - 拓扑顺序自动（z42.core 先编，其余依赖它的后编）
#
# 不复制到 artifacts/z42/libs/——拷贝步骤移到 package.sh（仅打包分发版时执行）。
# 编译 stdlib 间互相依赖（如 z42.collections → z42.core）通过 PackageCompiler.
# BuildLibsDirs 扫一层 <member>/dist/ 子目录解决，无需扁平布局。
#
# Usage:
#   ./scripts/build-stdlib.sh                 # release build, uses dotnet run
#   ./scripts/build-stdlib.sh --use-dist      # uses packaged z42c from artifacts/z42/bin/
#
# Output:
#   artifacts/libraries/<lib>/dist/<lib>.zpkg   (workspace 直接产物)
#   artifacts/libraries/<lib>/cache/...         (中间产物)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
WS_DIR="$ROOT/src/libraries"
DIST_ROOT="$ROOT/artifacts/libraries"

USE_DIST=false
for arg in "$@"; do
    if [ "$arg" = "--use-dist" ]; then
        USE_DIST=true
    fi
done

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

# 校验产物存在
ok=0
fail=0
for lib in "${LIBS[@]}"; do
    zpkg="$DIST_ROOT/$lib/dist/$lib.zpkg"
    if [[ -f "$zpkg" && -s "$zpkg" ]]; then
        size=$(wc -c < "$zpkg" | tr -d ' ')
        echo "    ✓ $lib.zpkg ($size bytes) → $zpkg"
        ((ok++)) || true
    else
        echo "    ✗ $lib.zpkg — workspace product missing at $zpkg"
        ((fail++)) || true
    fi
done

echo ""
echo "build-stdlib: $ok succeeded, $fail failed"
echo "(distribution copy 已移到 package.sh，仅打包分发版时同步到 artifacts/z42/libs/)"
if [[ $fail -gt 0 ]]; then
    exit 1
fi
