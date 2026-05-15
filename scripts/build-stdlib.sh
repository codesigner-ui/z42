#!/usr/bin/env bash
# build-stdlib.sh — compile z42 standard library packages via workspace mode.
#
# Workspace 配置：src/libraries/z42.workspace.toml
# 产物布局（redesign-artifact-layout, 2026-05-12）：
#   artifacts/build/libraries/<lib>/<profile>/dist/<lib>.zpkg
#   artifacts/build/libraries/<lib>/<profile>/cache/<file>.zbc
#
# 不再同步到 artifacts/z42/libs/（该路径已废弃）。
# Dev VM 通过 resolve_libs_dir() 自动扫 artifacts/build/libraries/<lib>/<profile>/dist/。
# 分发包：./scripts/package.sh 组装 artifacts/packages/z42-...
#
# Usage:
#   ./scripts/build-stdlib.sh                 # release build, uses dotnet run
#   ./scripts/build-stdlib.sh --use-dist      # uses packaged z42c from artifacts/packages/<host-pkg>/bin/

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
WS_DIR="$ROOT/src/libraries"
BUILD_LIBS_ROOT="$ROOT/artifacts/build/libraries"
PROFILE="release"

USE_DIST=false
for arg in "$@"; do
    if [ "$arg" = "--use-dist" ]; then
        USE_DIST=true
    elif [ "$arg" = "--debug" ]; then
        PROFILE="debug"
    fi
done

if [ "$USE_DIST" = true ]; then
    # 找一个 host 平台已组装的 package（如有多个取最新 mtime）
    Z42C=$(ls -t "$ROOT"/artifacts/packages/z42-*/bin/z42c 2>/dev/null | head -1)
    if [ -z "${Z42C:-}" ] || [ ! -x "$Z42C" ]; then
        echo "error: no packaged z42c found under artifacts/packages/*/bin/"
        echo "       Run: ./scripts/package.sh"
        exit 1
    fi
    echo "Using packaged compiler: $Z42C"
    COMPILER_CMD=("$Z42C")
else
    echo "Using dotnet run compiler"
    COMPILER_CMD=(dotnet run --project "$ROOT/src/compiler/z42.Driver" --)
fi

LIBS=(z42.core z42.io z42.io.binary z42.math z42.text z42.encoding z42.collections z42.test z42.time z42.toml z42.json z42.random z42.uri z42.diagnostics z42.regex)

# Workspace 模式：cd 到 src/libraries 触发 workspace 发现；
# z42c build --workspace --release 编译所有 default-members
echo "  building stdlib workspace ($PROFILE, all members)"
if [ "$PROFILE" = "release" ]; then
    ( cd "$WS_DIR" && "${COMPILER_CMD[@]}" build --workspace --release )
else
    ( cd "$WS_DIR" && "${COMPILER_CMD[@]}" build --workspace )
fi

# 校验产物存在
ok=0
fail=0
for lib in "${LIBS[@]}"; do
    zpkg="$BUILD_LIBS_ROOT/$lib/$PROFILE/dist/$lib.zpkg"
    if [[ -f "$zpkg" && -s "$zpkg" ]]; then
        size=$(wc -c < "$zpkg" | tr -d ' ')
        echo "    ✓ $lib.zpkg ($size bytes)"
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

echo ""

# 在 artifacts/build/libs/<profile>/ 下做扁平视图，供 VM 单目录 lookup
# （resolve_libs_dir() 当前接 PathBuf 单一目录）。每个文件用硬链接（cp -l）
# 避免重复占盘；fallback 到 cp。
FLAT_DIR="$ROOT/artifacts/build/libs/$PROFILE"
mkdir -p "$FLAT_DIR"
rm -f "$FLAT_DIR"/*.zpkg "$FLAT_DIR"/*.zsym 2>/dev/null || true
for lib in "${LIBS[@]}"; do
    src_dir="$BUILD_LIBS_ROOT/$lib/$PROFILE/dist"
    for f in "$src_dir"/*.zpkg "$src_dir"/*.zsym; do
        [ -f "$f" ] || continue
        cp -l "$f" "$FLAT_DIR/" 2>/dev/null || cp "$f" "$FLAT_DIR/"
    done
done
echo "  flat view: $FLAT_DIR/   (VM single-dir lookup target)"

# Namespace index — mobile / wasm 默认 resolver 用，map namespace → zpkg file.
# v1 hardcode；docs/spec/archive/2026-05-12-fix-bundle-resolver-namespace-index/
# 的 design.md Deferred 段记录"auto-derive from zpkg metadata"作为下一步候选。
# 加新 stdlib lib 时同步更新本 heredoc。
INDEX="$FLAT_DIR/index.json"
cat > "$INDEX" <<'EOF'
{
  "z42.core":        "z42.core.zpkg",
  "Std":             "z42.core.zpkg",
  "Std.Exceptions":  "z42.core.zpkg",
  "Std.IO":          "z42.io.zpkg",
  "Std.Math":        "z42.math.zpkg",
  "Std.Text":        "z42.text.zpkg",
  "Std.Encoding":    "z42.encoding.zpkg",
  "Std.Collections": "z42.collections.zpkg",
  "Std.Test":        "z42.test.zpkg",
  "Std.Time":        "z42.time.zpkg",
  "Std.Toml":        "z42.toml.zpkg",
  "Std.Json":        "z42.json.zpkg",
  "Std.Random":      "z42.random.zpkg",
  "Std.Uri":         "z42.uri.zpkg",
  "Std.Binary":      "z42.io.binary.zpkg",
  "Std.Diagnostics": "z42.diagnostics.zpkg",
  "Std.Regex":       "z42.regex.zpkg"
}
EOF
echo "  index:     $INDEX"

echo "  分发版打包用 ./scripts/package.sh."
