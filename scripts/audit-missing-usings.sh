#!/usr/bin/env bash
# scripts/audit-missing-usings.sh — bootstrap stub for the z42 implementation.
#
# 2026-05-17 port-audit-missing-usings: 主体迁移到 scripts/audit-missing-usings.z42。
# 本 bash stub 仅作 toolchain bootstrap，永远不会消失（self-host 边界）。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# Build only the driver project in Debug — matches regen-golden-tests.sh /
# test-vm.sh; pre-priming Release trips MSB3492 in CI.
dotnet build src/compiler/z42.Driver/z42.Driver.csproj >/dev/null
cargo build --manifest-path src/runtime/Cargo.toml --release --quiet
./scripts/build-stdlib.sh >/dev/null

exec dotnet run --project src/compiler/z42.Driver --verbosity quiet --no-build -- \
    run scripts/audit-missing-usings.z42
