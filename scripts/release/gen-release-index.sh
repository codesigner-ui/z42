#!/usr/bin/env bash
# Generate <dist>/release-index.json from <dist>/SHA256SUMS — the launcher's supply
# contract (z42 install / workload install read it). Shared by release.yml (tagged)
# and ci.yml publish-nightly (rolling), so the schema lives in ONE place.
#
#   gen-release-index.sh <LABEL> [<dist>] [<channel>] [<tag>] [<version>]
#
# Archive names are uniform: z42-<kind>-<LABEL>-<rid>{.tar.gz,.zip}. LABEL = <ver>
# (tagged) or "nightly". version/channel/tag are the manifest metadata fields:
#   tagged : LABEL=0.3.0  channel=stable  tag=v0.3.0  version=0.3.0
#   nightly: LABEL=nightly channel=nightly tag=nightly version=nightly
set -euo pipefail
L="${1:?usage: gen-release-index.sh <LABEL> [dist] [channel] [tag] [version]}"
DIST="${2:-dist}"
CHANNEL="${3:-stable}"
TAG="${4:-v$L}"
VERSION="${5:-$L}"
ts="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

get_sha() { grep " $1\$" "${DIST}/SHA256SUMS" | awk '{print $1}'; }

# Desktop: sdk (z42c+vm+libs) + runtime (vm+libs).
sha_sdk_lx64="$(get_sha "z42-sdk-${L}-linux-x64.tar.gz")"
sha_rt_lx64="$(get_sha  "z42-runtime-${L}-linux-x64.tar.gz")"
sha_sdk_la64="$(get_sha "z42-sdk-${L}-linux-arm64.tar.gz")"
sha_rt_la64="$(get_sha  "z42-runtime-${L}-linux-arm64.tar.gz")"
sha_sdk_mac="$(get_sha  "z42-sdk-${L}-macos-arm64.tar.gz")"
sha_rt_mac="$(get_sha   "z42-runtime-${L}-macos-arm64.tar.gz")"
sha_sdk_win="$(get_sha  "z42-sdk-${L}-windows-x64.zip")"
sha_rt_win="$(get_sha   "z42-runtime-${L}-windows-x64.zip")"
# Platform runtime packs (per RID).
sha_ios="$(get_sha    "z42-runtime-${L}-ios-arm64.tar.gz")"
sha_iossim="$(get_sha "z42-runtime-${L}-iossim-arm64.tar.gz")"
sha_aarm64="$(get_sha "z42-runtime-${L}-android-arm64.tar.gz")"
sha_ax64="$(get_sha   "z42-runtime-${L}-android-x64.tar.gz")"
sha_wasm="$(get_sha   "z42-runtime-${L}-browser-wasm.tar.gz")"
# Workload tooling (ios/android/wasm bundle per-RID runtime packs; desktop = single).
sha_wl_ios="$(get_sha     "z42-workload-${L}-ios.tar.gz")"
sha_wl_android="$(get_sha "z42-workload-${L}-android.tar.gz")"
sha_wl_wasm="$(get_sha    "z42-workload-${L}-wasm.tar.gz")"
sha_wl_dt="$(get_sha      "z42-workload-${L}-desktop.tar.gz")"

for var in sha_sdk_lx64 sha_rt_lx64 sha_sdk_la64 sha_rt_la64 \
           sha_sdk_mac sha_rt_mac sha_sdk_win sha_rt_win \
           sha_ios sha_iossim sha_aarm64 sha_ax64 sha_wasm \
           sha_wl_ios sha_wl_android sha_wl_wasm sha_wl_dt; do
  [ -n "${!var}" ] || { echo "release-index: missing sha256 for $var" >&2; exit 1; }
done

jq -n \
  --argjson schema 1 \
  --arg version "$VERSION" --arg channel "$CHANNEL" --arg tag "$TAG" --arg published "$ts" \
  --arg L "$L" \
  --arg sha_sdk_lx64 "$sha_sdk_lx64" --arg sha_rt_lx64 "$sha_rt_lx64" \
  --arg sha_sdk_la64 "$sha_sdk_la64" --arg sha_rt_la64 "$sha_rt_la64" \
  --arg sha_sdk_mac  "$sha_sdk_mac"  --arg sha_rt_mac  "$sha_rt_mac"  \
  --arg sha_sdk_win  "$sha_sdk_win"  --arg sha_rt_win  "$sha_rt_win"  \
  --arg sha_ios "$sha_ios" --arg sha_iossim "$sha_iossim" \
  --arg sha_aarm64 "$sha_aarm64" --arg sha_ax64 "$sha_ax64" --arg sha_wasm "$sha_wasm" \
  --arg sha_wl_ios "$sha_wl_ios" --arg sha_wl_android "$sha_wl_android" \
  --arg sha_wl_wasm "$sha_wl_wasm" --arg sha_wl_dt "$sha_wl_dt" \
  '{
    schema: $schema,
    version: $version,
    channel: $channel,
    tag: $tag,
    published: $published,
    runtimes: {
      "linux-x64": {
        sdk:     { archive: ("z42-sdk-" + $L + "-linux-x64.tar.gz"),     sha256: $sha_sdk_lx64 },
        runtime: { archive: ("z42-runtime-" + $L + "-linux-x64.tar.gz"), sha256: $sha_rt_lx64 }
      },
      "linux-arm64": {
        sdk:     { archive: ("z42-sdk-" + $L + "-linux-arm64.tar.gz"),     sha256: $sha_sdk_la64 },
        runtime: { archive: ("z42-runtime-" + $L + "-linux-arm64.tar.gz"), sha256: $sha_rt_la64 }
      },
      "macos-arm64": {
        sdk:     { archive: ("z42-sdk-" + $L + "-macos-arm64.tar.gz"),     sha256: $sha_sdk_mac },
        runtime: { archive: ("z42-runtime-" + $L + "-macos-arm64.tar.gz"), sha256: $sha_rt_mac }
      },
      "windows-x64": {
        sdk:     { archive: ("z42-sdk-" + $L + "-windows-x64.zip"),         sha256: $sha_sdk_win },
        runtime: { archive: ("z42-runtime-" + $L + "-windows-x64.zip"),     sha256: $sha_rt_win }
      },
      "ios-arm64":    { runtime: { archive: ("z42-runtime-" + $L + "-ios-arm64.tar.gz"),    sha256: $sha_ios    } },
      "iossim-arm64": { runtime: { archive: ("z42-runtime-" + $L + "-iossim-arm64.tar.gz"), sha256: $sha_iossim } },
      "android-arm64":{ runtime: { archive: ("z42-runtime-" + $L + "-android-arm64.tar.gz"),sha256: $sha_aarm64 } },
      "android-x64":  { runtime: { archive: ("z42-runtime-" + $L + "-android-x64.tar.gz"), sha256: $sha_ax64   } },
      "browser-wasm": { runtime: { archive: ("z42-runtime-" + $L + "-browser-wasm.tar.gz"), sha256: $sha_wasm  } }
    },
    workloads: {
      "ios":     { archive: ("z42-workload-" + $L + "-ios.tar.gz"),     sha256: $sha_wl_ios,     host: ["macos-arm64"], runtimes: ["ios-arm64","iossim-arm64"] },
      "android": { archive: ("z42-workload-" + $L + "-android.tar.gz"), sha256: $sha_wl_android, host: ["macos-arm64","linux-x64","linux-arm64","windows-x64"], runtimes: ["android-arm64","android-x64"] },
      "wasm":    { archive: ("z42-workload-" + $L + "-wasm.tar.gz"),    sha256: $sha_wl_wasm,    host: ["*"], runtimes: ["browser-wasm"] },
      "desktop": { archive: ("z42-workload-" + $L + "-desktop.tar.gz"), sha256: $sha_wl_dt,      host: ["*"], runtimes: [] }
    }
  }' > "${DIST}/release-index.json"
echo "--- release-index.json ---"
cat "${DIST}/release-index.json"
