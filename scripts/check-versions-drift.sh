#!/usr/bin/env bash
# scripts/check-versions-drift.sh — bootstrap stub for the z42 implementation.
#
# 2026-05-16 port-check-versions-drift: 主体迁移到 scripts/check-versions-drift.z42。
# 2026-05-17 add-z42c-run-script: 用 `z42c run <script.z42>` 替代手动 compile +
#   exec — bootstrap 从 ~15 行降到 ~8 行。
# 本 bash stub 永远不会消失（self-host 边界：toolchain build 不能用 z42 自启动）。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

dotnet build -c Release src/compiler/z42.slnx >/dev/null 2>&1
cargo build --manifest-path src/runtime/Cargo.toml --release --quiet
./scripts/build-stdlib.sh >/dev/null 2>&1

exec dotnet run --project src/compiler/z42.Driver -c Release --verbosity quiet --no-build -- \
    run scripts/check-versions-drift.z42 "$@"
