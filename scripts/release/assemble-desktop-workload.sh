#!/usr/bin/env bash
# Merge the 4 per-RID desktop-workload pieces (each carries its apphost-<rid>) into
# ONE RID-agnostic z42-workload-<LABEL>-desktop.tar.gz so any host can `publish
# --rid <target>` (cross-platform apphost output). Removes the per-RID intermediates
# so only the merged archive ships. Shared by release.yml (tagged) + ci.yml nightly.
#
#   assemble-desktop-workload.sh <LABEL> [<dist-dir>]
#     LABEL = <ver> (e.g. 0.3.0) for a tagged release, or "nightly".
set -euo pipefail
LABEL="${1:?usage: assemble-desktop-workload.sh <LABEL> [dist-dir]}"
DIST="${2:-dist}"

merged="$(mktemp -d)/z42-workload-${LABEL}-desktop"
mkdir -p "$merged"
for rid in linux-x64 linux-arm64 macos-arm64 windows-x64; do
  ar="${DIST}/z42-workload-${LABEL}-desktop-${rid}.tar.gz"
  [ -f "$ar" ] || ar="${DIST}/z42-workload-${LABEL}-desktop-${rid}.zip"
  [ -f "$ar" ] || { echo "assemble-desktop-workload: missing piece for ${rid}" >&2; exit 1; }
  tmp="$(mktemp -d)"
  if [[ "$ar" == *.zip ]]; then unzip -q "$ar" -d "$tmp"; else tar -xzf "$ar" -C "$tmp"; fi
  cp "$tmp"/apphost-"${rid}"* "$merged"/
done

cat > "${merged}/manifest.toml" <<TOML
[package]
name        = "z42-workload-desktop"
kind        = "workload-tooling"
version     = "${LABEL}"
abi-version = 1
[contents.platform]
apphost-prefix = "apphost-"
host           = ["*"]
runtime-pack   = ""
[compat]
host-min-version = "${LABEL}"
TOML

rm -f "${DIST}"/z42-workload-"${LABEL}"-desktop-*.tar.gz "${DIST}"/z42-workload-"${LABEL}"-desktop-*.zip
tar -C "$merged" -czf "${DIST}/z42-workload-${LABEL}-desktop.tar.gz" .
echo "✓ merged desktop workload → z42-workload-${LABEL}-desktop.tar.gz ($(ls "$merged" | tr '\n' ' '))"
