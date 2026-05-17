#!/usr/bin/env bash
# scripts/build-stdlib.sh — bootstrap stub for the z42 implementation.
#
# 2026-05-17 port-build-stdlib: 主体迁移到 scripts/build-stdlib.z42。
# bash stub 处理 self-host 边界：
#   1. 编译 toolchain（dotnet + cargo）
#   2. 鸡生蛋 primer — 若 stdlib zpkgs 缺失（首次 clone），先用 raw z42c 构建一遍
#      + 简易 flat view 让 z42 script 能 compile
#   3. 调度到 scripts/build-stdlib.z42（自身重做完整工作流：build + verify + flat
#      view + index.json）
#
# Usage:
#   ./scripts/build-stdlib.sh                 # release build
#   ./scripts/build-stdlib.sh --debug         # debug build
#   ./scripts/build-stdlib.sh --use-dist      # uses packaged z42c
#
# Args 透传给 z42 script。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# Toolchain — build only the driver project in Debug (matches downstream
# regen-golden-tests.sh / test-vm.sh config; pre-priming Release would leave
# obj/ caches inconsistent with their Debug build and trip MSB3492). slnx-wide
# build also pulls Microsoft.CodeCoverage targets on z42.Tests which add their
# own MSB3492 surface — single-project keeps the cache footprint minimal.
dotnet build src/compiler/z42.Driver/z42.Driver.csproj >/dev/null
cargo build --manifest-path src/runtime/Cargo.toml --release --quiet

# Chicken-and-egg primer: build-stdlib.z42 self uses `using Std.IO/Regex/Cli;`
# 若 stdlib 不存在（fresh clone）→ z42c run 编译本 script 时 import 失败。
# 用 raw z42c 跑一次 workspace build + 拷 minimal flat view。常见情况（stdlib
# 已存在）整个 if 块跳过，z42 script 直接接管 + incremental rebuild。
if [ ! -f artifacts/build/libs/release/z42.core.zpkg ]; then
    echo "  (primer: bootstrapping stdlib for the first time)"
    ( cd src/libraries && dotnet run --project ../compiler/z42.Driver \
        --verbosity quiet --no-build -- build --workspace --release ) >/dev/null
    mkdir -p artifacts/build/libs/release
    for d in artifacts/build/libraries/*/release/dist; do
        cp -l "$d"/*.zpkg "$d"/*.zsym artifacts/build/libs/release/ 2>/dev/null || \
            cp "$d"/*.zpkg "$d"/*.zsym artifacts/build/libs/release/ 2>/dev/null || true
    done
fi

exec dotnet run --project src/compiler/z42.Driver --verbosity quiet --no-build -- \
    run scripts/build-stdlib.z42 "$@"
