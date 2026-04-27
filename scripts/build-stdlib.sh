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
LIBS_OUT="$ROOT/artifacts/z42/libs"

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

LIBS=(z42.core z42.io z42.math z42.text z42.collections z42.test)

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
if [[ $fail -gt 0 ]]; then
    exit 1
fi

# 同步到 VM 加载路径 artifacts/z42/libs/。
#
# VM 通过 main.rs::resolve_libs_dir() 在以下顺序找 stdlib：
#   1. $Z42_LIBS    2. <bin>/../libs/    3. $cwd/artifacts/z42/libs/
# 早期 build-stdlib.sh 不写这里，改完 stdlib 后必须额外跑 package.sh 才能让 dev
# 模式 VM / golden test 看到新 zpkg；这是 wave1-path-script (2026-04-27) 实施时
# 反复踩到的坑。现在每次 stdlib build 后自动 cp 一遍，避免不一致。
#
# package.sh 仍负责"完整分发版打包"（含 z42c / z42vm 单文件 + libs/），与本步
# 互不冲突 —— package.sh 自己也调 build-stdlib.sh，cp 重复执行无副作用。
mkdir -p "$LIBS_OUT"
echo ""
echo "  syncing → $LIBS_OUT/"
for lib in "${LIBS[@]}"; do
    src="$DIST_ROOT/$lib/dist/$lib.zpkg"
    dst="$LIBS_OUT/$lib.zpkg"
    if [[ -f "$src" && -s "$src" ]]; then
        cp "$src" "$dst"
        echo "    ✓ $lib.zpkg"
    fi
done
