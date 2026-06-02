#!/usr/bin/env bash
# scripts/audit-missing-usings.sh — bootstrap stub for the z42 implementation.
#
# 2026-05-17 port-audit-missing-usings: 主体迁移到 scripts/audit-missing-usings.z42。
# 2026-06-03 add-z42-launcher cutover: 编译成 Exe-zpkg + 经 `z42` launcher 运行。
# 本 bash stub 仅作 toolchain bootstrap，永远不会消失（self-host 边界）。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

dotnet build src/compiler/z42.Driver/z42.Driver.csproj >/dev/null
cargo build --manifest-path src/runtime/Cargo.toml --release --quiet
./scripts/build-stdlib.sh >/dev/null

source "$ROOT/scripts/_lib/launcher-env.sh"
setup_launcher_env "$ROOT" release
Z42_LIBS="$ROOT/artifacts/build/libs/release" dotnet run --project src/compiler/z42.Driver \
    --verbosity quiet --no-build -- build scripts/audit-missing-usings.z42.toml --release >/dev/null

exec "$Z42_LAUNCHER" run "$ROOT/scripts/dist/audit-missing-usings.zpkg"
