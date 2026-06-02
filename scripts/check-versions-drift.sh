#!/usr/bin/env bash
# scripts/check-versions-drift.sh — bootstrap stub for the z42 implementation.
#
# 2026-05-16 port-check-versions-drift: 主体迁移到 scripts/check-versions-drift.z42。
# 2026-05-17 add-z42c-run-script: 用 `z42c run <script.z42>` 替代手动 compile + exec。
# 2026-06-03 add-z42-launcher cutover: 编译成 Exe-zpkg + 经 `z42` launcher 运行，
#   取代 `z42c run`（编译器不再兼当 runner；参数走 z42vm `-- args` 透传）。
# 本 bash stub 永远不会消失（self-host 边界：toolchain build 不能用 z42 自启动）。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

dotnet build src/compiler/z42.Driver/z42.Driver.csproj >/dev/null
cargo build --manifest-path src/runtime/Cargo.toml --release --quiet
./scripts/build-stdlib.sh >/dev/null

# Compile this script to an Exe-mode zpkg, then run it through the launcher.
source "$ROOT/scripts/_lib/launcher-env.sh"
setup_launcher_env "$ROOT" release
Z42_LIBS="$ROOT/artifacts/build/libs/release" dotnet run --project src/compiler/z42.Driver \
    --verbosity quiet --no-build -- build scripts/check-versions-drift.z42.toml --release >/dev/null

exec "$Z42_LAUNCHER" run "$ROOT/scripts/dist/check-versions-drift.zpkg" -- "$@"
