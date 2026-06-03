#!/usr/bin/env bash
# scripts/regen-golden-tests.sh — bootstrap stub for the z42 implementation.
#
# 2026-05-27 port-regen-golden-tests: 主体迁移到 scripts/regen-golden-tests.z42.
# bash stub 处理 self-host 边界：
#   1. cd 到 repo root
#   2. 解析 legacy CLI（`release` 位置参数 + `--no-stdlib` flag）
#   3. 编译 toolchain（dotnet build z42.Driver）
#   4. 默认调用 build-stdlib.sh（除非 --no-stdlib）— 保证 golden test 编译
#      面对的是最新 stdlib（fix-test-vm-stale-artifacts 2026-05-04）
#   5. 调度到 scripts/regen-golden-tests.z42（接管枚举 + 编译 + 报告）
#
# Usage:
#   ./scripts/regen-golden-tests.sh                  # debug build, rebuild stdlib
#   ./scripts/regen-golden-tests.sh release          # release build
#   ./scripts/regen-golden-tests.sh --no-stdlib      # skip stdlib rebuild
#   ./scripts/regen-golden-tests.sh release --no-stdlib

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

# Translate legacy positional CLI → z42 flag form.
BUILD_CONFIG="Debug"
REBUILD_STDLIB=true
Z42_ARGS=()
for arg in "$@"; do
    case "$arg" in
        release)      BUILD_CONFIG="Release"; Z42_ARGS+=(--release) ;;
        --no-stdlib)  REBUILD_STDLIB=false ;;
    esac
done

if [ "$REBUILD_STDLIB" = true ]; then
    # build-stdlib.sh internally invokes the C# compiler (dotnet run --project
    # z42.Driver), which transitively rebuilds the compiler if stale. After
    # producing dist/<lib>.zpkg it syncs into artifacts/build/libraries/dist/release/ so the VM
    # loader sees the current stdlib. Skip via --no-stdlib when the caller has
    # already done this (e.g. test-vm.sh re-entry).
    "$SCRIPT_DIR/build-stdlib.sh"
    echo ""
fi

# Build only the compiler driver (z42c.dll) — not the full solution.
# Building z42.slnx here triggers Microsoft.CodeCoverage targets on
# z42.Tests which fail with MSB3492 when the mapping file from a prior
# build exists. Reuse a prior artifact when present (CI pipelines pre-build
# the slnx in an earlier wave).
DRIVER_DLL="artifacts/build/compiler/z42.Driver/bin/z42c.dll"
if [ ! -f "$DRIVER_DLL" ]; then
    echo "Building compiler (${BUILD_CONFIG})..."
    dotnet build -q src/compiler/z42.Driver/z42.Driver.csproj -c "$BUILD_CONFIG"
fi

# add-z42-launcher cutover (2026-06-03): compile to Exe-zpkg + run via the
# `z42` launcher (args forwarded after `--`; the script reads clean argv).
# Ensure z42vm exists (the --no-stdlib path skips build-stdlib.sh which would
# otherwise build it) — the launcher runtime needs it.
cargo build --manifest-path src/runtime/Cargo.toml --release --quiet
source "$ROOT/scripts/_lib/launcher-env.sh"
setup_launcher_env "$ROOT" release
Z42_LIBS="$ROOT/artifacts/build/libraries/dist/release" dotnet run --project src/compiler/z42.Driver \
    --verbosity quiet --no-build -- build scripts/regen-golden-tests.z42.toml --release >/dev/null

exec "$Z42_LAUNCHER" run "$ROOT/scripts/dist/regen-golden-tests.zpkg" -- "${Z42_ARGS[@]+"${Z42_ARGS[@]}"}"
