#!/usr/bin/env bash
# ci-stage-toolchain.sh — stage the C#-free-built compiler artifacts (z42c dist +
# stdlib dist + xtask.zpkg) into <dest> for upload as the `toolchain-<os>` CI
# artifact. ONLY the .zpkg/.zsym products are staged — NOT z42vm or any cargo build
# intermediate. Consumer jobs (via the xtask-bootstrap-artifact action) download this
# back into the repo root and `cargo build` their own host z42vm, so they never need
# dotnet to obtain a compiler. Keeps the artifact tiny (~a few MB of bytecode).
#
# Usage: scripts/ci-stage-toolchain.sh <dest-dir>
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
dest="${1:?usage: ci-stage-toolchain.sh <dest-dir>}"

# stdlib: flat dist (Z42_LIBS lookup) + per-member dist (warm-seed self-build inputs)
mkdir -p "$dest/artifacts/build/libraries"
cp -r artifacts/build/libraries/dist "$dest/artifacts/build/libraries/"
for d in artifacts/build/libraries/*/release/dist; do
  [ -d "$d" ] || continue
  m=$(basename "$(dirname "$(dirname "$d")")")
  mkdir -p "$dest/artifacts/build/libraries/$m/release/dist"
  cp "$d"/*.zpkg "$dest/artifacts/build/libraries/$m/release/dist/" 2>/dev/null || true
  cp "$d"/*.zsym "$dest/artifacts/build/libraries/$m/release/dist/" 2>/dev/null || true
done

# z42c compiler: per-member dist (7 zpkgs incl. z42c.driver.zpkg = the warm seed)
for d in artifacts/build/z42c/*/release/dist; do
  [ -d "$d" ] || continue
  m=$(basename "$(dirname "$(dirname "$d")")")
  mkdir -p "$dest/artifacts/build/z42c/$m/release/dist"
  cp "$d"/*.zpkg "$dest/artifacts/build/z42c/$m/release/dist/" 2>/dev/null || true
  cp "$d"/*.zsym "$dest/artifacts/build/z42c/$m/release/dist/" 2>/dev/null || true
done

# xtask dev CLI
mkdir -p "$dest/artifacts/xtask"
cp artifacts/xtask/xtask.zpkg "$dest/artifacts/xtask/"

echo "staged C#-free toolchain → $dest"
( cd "$dest" && find artifacts -type f | sort )
