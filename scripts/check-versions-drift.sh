#!/usr/bin/env bash
# scripts/check-versions-drift.sh — bootstrap stub for the z42 implementation.
#
# 2026-05-16 port-check-versions-drift: 主体迁移到 scripts/check-versions-drift.z42。
# 本 bash 文件仅作 self-host 边界 — toolchain build 不能用 z42 自启动，所以
# 这层 ~10 行 stub 永远不会消失：
#   1. dotnet build z42c
#   2. cargo build z42vm
#   3. ./scripts/build-stdlib.sh
#   4. compile script → .zbc → exec z42vm

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# Toolchain — silent rebuild; no-op if up-to-date
dotnet build -c Release src/compiler/z42.slnx >/dev/null 2>&1
cargo build --manifest-path src/runtime/Cargo.toml --release --quiet
./scripts/build-stdlib.sh >/dev/null 2>&1

# Compile + run script
TMP=$(mktemp -d)
trap "rm -rf $TMP" EXIT
dotnet run --project src/compiler/z42.Driver -c Release --verbosity quiet -- \
    scripts/check-versions-drift.z42 --emit zbc -o "$TMP/cvd.zbc" >/dev/null 2>&1
exec ./artifacts/build/runtime/release/z42vm \
    "$TMP/cvd.zbc" Z42CheckVersionsDriftScript.Main
