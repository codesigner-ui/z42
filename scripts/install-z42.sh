#!/usr/bin/env bash
# install-z42.sh — z42-bootstrap (2026-06-12).
#
# Download the prebuilt z42 launcher package from GitHub Releases into a
# PROJECT-LOCAL <repo>/.z42 (isolated; gitignored). This is the ONE native
# bootstrap that stays — a chicken-and-egg primer: you need a working z42 to
# run the z42-implemented dev tooling (xtask + migrated scripts), so this
# fetches it. Everything else is z42.
#
# Thin by design: this script only bootstraps; ALL subsequent runtime
# management (install/update/workload) goes through `z42 install` once z42
# is running. Do NOT add features here — keep it minimal so migration to
# a native launcher (NativeAOT, deferred) is cheap.
#
# Version comes from versions.toml [toolchain.z42].launcher:
#   "nightly" (default) → tag `nightly`; re-download when the release changes
#   "0.1.0"             → tag `v0.1.0`;  download once (immutable)
#
# Download strategy (manifest-first, SHA256SUMS fallback):
#   1. Try release-index.json from the release — provides archive name + sha256
#      per RID, and a published timestamp for nightly staleness (no GitHub API).
#   2. If release-index.json absent (older releases), fall back to the
#      naming convention (z42-<ver>-<rid>.tar.gz) + SHA256SUMS best-effort.
#   The fallback is a compatibility shim; once all active channels publish
#   release-index.json it will be removed (install-legacy-sha256sums-fallback).
#
# Windows: use install-z42.bat (.zip). macOS Finder: double-click
# install-z42.command (which execs this).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="$REPO/.z42"
SLUG="codesigner-ui/z42"
STAMP="$DEST/.bootstrap-stamp"

# ── 1. version from versions.toml [toolchain.z42].launcher ──────────────────
VER="$(awk '
  /^\[toolchain\.z42\]/ {inblock=1; next}
  /^\[/ {inblock=0}
  inblock && /^launcher/ {split($0, a, "\""); print a[2]; exit}
' "$REPO/versions.toml" 2>/dev/null)"
VER="${VER:-nightly}"
if [ "$VER" = "nightly" ]; then TAG="nightly"; else TAG="v$VER"; fi

# ── 2. host RID ─────────────────────────────────────────────────────────────
os="$(uname -s)"; arch="$(uname -m)"
case "$os/$arch" in
  Darwin/arm64)              RID="macos-arm64" ;;
  Linux/x86_64)              RID="linux-x64" ;;
  Linux/aarch64|Linux/arm64) RID="linux-arm64" ;;
  *) echo "install-z42: unsupported host $os/$arch" >&2
     echo "  (Windows: use install-z42.bat; supported: macos-arm64 / linux-x64 / linux-arm64)" >&2
     exit 1 ;;
esac

# ── 3. pinned-version fast path: skip if already installed ──────────────────
# (no network access for pinned versions that are already present)
if [ "$VER" != "nightly" ] && [ -f "$STAMP" ] \
    && grep -q "^$VER:$RID:" "$STAMP" 2>/dev/null; then
  echo "install-z42: $VER / $RID already installed"
  exit 0
fi

# ── 4. resolve archive + sha256 + staleness id ──────────────────────────────
# Minimal JSON field extractors (no jq; manifest lines are key: "value" pairs)
_manifest_str() {   # _manifest_str <json> <key>  → first matching string value
  printf '%s' "$1" | grep -o "\"$2\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
    | head -1 | sed -E 's/.*"[^"]*"[[:space:]]*:[[:space:]]*"([^"]*)"/\1/'
}
_rid_field() {      # _rid_field <json> <rid> <field>
  printf '%s' "$1" | grep "\"$2\"" \
    | grep -o "\"$3\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
    | sed -E 's/.*"[^"]*"[[:space:]]*:[[:space:]]*"([^"]*)"/\1/'
}

MANIFEST_URL="https://github.com/$SLUG/releases/download/$TAG/release-index.json"
MANIFEST_JSON=""
MANIFEST_JSON="$(curl -fsSL "$MANIFEST_URL" 2>/dev/null)" || MANIFEST_JSON=""

ASSET=""
MANIFEST_SHA=""
WANT=""

if [ -n "$MANIFEST_JSON" ]; then
  # ── 4a. manifest path ─────────────────────────────────────────────────────
  MANIFEST_PUBLISHED="$(_manifest_str "$MANIFEST_JSON" "published")"
  ASSET="$(_rid_field "$MANIFEST_JSON" "$RID" "archive")"
  MANIFEST_SHA="$(_rid_field "$MANIFEST_JSON" "$RID" "sha256")"

  if [ -z "$ASSET" ]; then
    echo "install-z42: RID '$RID' not found in release-index.json" >&2
    echo "  (this RID may not be a supported host runtime)" >&2
    exit 1
  fi

  # Staleness: use manifest published timestamp (no GitHub API call needed)
  WANT="$VER:$RID:${MANIFEST_PUBLISHED:-unknown}"
  if [ -f "$STAMP" ] && [ -n "$MANIFEST_PUBLISHED" ] \
      && [ "$(cat "$STAMP" 2>/dev/null)" = "$WANT" ]; then
    echo "install-z42: .z42 up to date ($VER / $RID)"
    exit 0
  fi
  DOWNLOAD_NOTE="[manifest]"

else
  # ── 4b. fallback: naming convention + SHA256SUMS (legacy releases) ────────
  ASSET="z42-$VER-$RID.tar.gz"

  ID=""
  if [ "$VER" = "nightly" ]; then
    ID="$(curl -fsSL "https://api.github.com/repos/$SLUG/releases/tags/nightly" 2>/dev/null \
      | grep -m1 '"published_at"' | sed -E 's/.*"published_at": *"([^"]+)".*/\1/')" || true
  else
    ID="$TAG"
  fi
  WANT="$VER:$RID:${ID:-unknown}"
  if [ -f "$STAMP" ] && [ -n "$ID" ] && [ "$(cat "$STAMP" 2>/dev/null)" = "$WANT" ]; then
    echo "install-z42: .z42 up to date ($VER / $RID)"
    exit 0
  fi
  DOWNLOAD_NOTE="[SHA256SUMS fallback]"
fi

# ── 5. download ──────────────────────────────────────────────────────────────
URL="https://github.com/$SLUG/releases/download/$TAG/$ASSET"
echo "install-z42: fetching $ASSET ($TAG) → $DEST  $DOWNLOAD_NOTE"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
curl -fSL "$URL" -o "$TMP/$ASSET" || { echo "install-z42: download failed: $URL" >&2; exit 1; }

# ── 6. verify SHA256 ─────────────────────────────────────────────────────────
_sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
  else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

if [ -n "$MANIFEST_SHA" ]; then
  # Strict: sha256 from manifest
  got="$(_sha256 "$TMP/$ASSET")"
  [ "$MANIFEST_SHA" = "$got" ] \
    || { echo "install-z42: SHA256 mismatch for $ASSET" >&2; exit 1; }
  echo "install-z42: SHA256 ok"
else
  # Best-effort: from SHA256SUMS (may be absent on older releases)
  if curl -fsSL "https://github.com/$SLUG/releases/download/$TAG/SHA256SUMS" \
      -o "$TMP/SHA256SUMS" 2>/dev/null; then
    line="$(grep " $ASSET\$" "$TMP/SHA256SUMS" || grep "  $ASSET\$" "$TMP/SHA256SUMS" || true)"
    if [ -n "$line" ]; then
      want_hash="${line%% *}"
      got="$(_sha256 "$TMP/$ASSET")"
      [ "$want_hash" = "$got" ] \
        || { echo "install-z42: SHA256 mismatch for $ASSET" >&2; exit 1; }
      echo "install-z42: SHA256 ok"
    fi
  fi
fi

# ── 7. extract → install ─────────────────────────────────────────────────────
mkdir -p "$TMP/pkg"
tar -xzf "$TMP/$ASSET" -C "$TMP/pkg"

rm -rf "$DEST"; mkdir -p "$DEST"
cp -R "$TMP/pkg"/. "$DEST"/
# GitHub Actions artifact upload/download strips the executable bit; restore it.
for b in "$DEST/z42" "$DEST/bin/z42" "$DEST/bin/z42vm" "$DEST/bin/z42c"; do
  [ -f "$b" ] && chmod +x "$b" 2>/dev/null || true
done
echo "$WANT" > "$STAMP"

# Entry: post launcher-at-package-root the trampoline is at .z42/z42.
ENTRY="$DEST/z42"; [ -f "$ENTRY" ] || ENTRY="$DEST/bin/z42"
echo "install-z42: installed $VER / $RID → $DEST"
echo "  entry: $ENTRY   (add '$DEST' to PATH, or run via \$REPO/.z42/z42)"
