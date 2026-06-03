#!/usr/bin/env bash
# scripts/test-cross-zpkg.sh — bootstrap stub for the z42 implementation.
#
# 2026-05-27 port-test-cross-zpkg: 主体迁移到 scripts/test-cross-zpkg.z42.
# bash stub 处理 self-host 边界 + toolchain build：
#   1. cd 到 repo root
#   2. 解析 legacy CLI（位置参数 mode: interp | jit）→ --vm-mode flag
#   3. 编译 toolchain（dotnet build z42.slnx + cargo build z42vm）
#   4. 检查 stdlib zpkgs 是否存在；不存在则调 build-stdlib.sh
#   5. 调度到 scripts/test-cross-zpkg.z42（接管枚举 + 三阶段构建 + 运行 + 比对）
#
# Usage:
#   ./scripts/test-cross-zpkg.sh                 # interp mode (default)
#   ./scripts/test-cross-zpkg.sh jit             # jit mode
#
# Exit code: 0 if all tests pass, 1 otherwise.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

# Translate legacy positional mode → --vm-mode flag.
MODE="${1:-interp}"

# Build compiler + VM. Run noisily on first failure but suppress normal
# up-to-date chatter (matches prior bash behaviour).
echo "Building compiler + VM..."
dotnet build src/compiler/z42.slnx >/tmp/test-cross-zpkg-build.log 2>&1 || {
    cat /tmp/test-cross-zpkg-build.log; exit 1; }
cargo build -q --manifest-path src/runtime/Cargo.toml
echo ""

# Locate stdlib zpkgs (workspace stdlib output under artifacts/build/libraries/).
STDLIB_ROOT="$ROOT/artifacts/build/libraries"
if ! ls "$STDLIB_ROOT"/*/release/dist/*.zpkg >/dev/null 2>&1; then
    echo "Building stdlib (required for cross-zpkg tests)..."
    "$SCRIPT_DIR/build-stdlib.sh" >/dev/null
    echo ""
fi

# add-z42-launcher cutover (2026-06-03): compile to Exe-zpkg + run via the
# `z42` launcher. MODE stays an env var (legit config, inherited by the
# spawned z42vm) — not the old argv hack.
source "$ROOT/scripts/_lib/launcher-env.sh"
setup_launcher_env "$ROOT" debug
Z42_LIBS="$ROOT/artifacts/build/libraries/dist/release" dotnet run --project src/compiler/z42.Driver \
    --verbosity quiet --no-build -- build scripts/test-cross-zpkg.z42.toml --release >/dev/null

exec env Z42_VM_MODE="$MODE" "$Z42_LAUNCHER" run "$ROOT/scripts/dist/test-cross-zpkg.zpkg"
