#!/usr/bin/env bash
# install-z42.sh — z42-bootstrap (2026-06-04).
#
# Download the prebuilt z42 launcher package from GitHub Releases into a
# PROJECT-LOCAL <repo>/.z42 (isolated; gitignored). This is the ONE native
# bootstrap that stays — a chicken-and-egg primer: you need a working z42 to
# run the z42-implemented dev tooling (xtask + migrated scripts), so this
# fetches it. Everything else is z42.
#
# Version comes from versions.toml [toolchain.z42].launcher:
#   "nightly" (default) → tag `nightly`; re-download when the release changes
#   "0.1.0"             → tag `v0.1.0`;  download once (immutable)
#
# Runs a version check every invocation: nightly compares the release's
# published_at against a stored stamp and only re-downloads when it changed;
# a pinned version is skipped once installed.
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
  inblock && /^launcher/ {gsub(/.*"|".*/,""); print; exit}
' "$REPO/versions.toml" 2>/dev/null)"
VER="${VER:-nightly}"
if [ "$VER" = "nightly" ]; then TAG="nightly"; else TAG="v$VER"; fi

# ── 2. host RID ─────────────────────────────────────────────────────────────
os="$(uname -s)"; arch="$(uname -m)"
case "$os/$arch" in
  Darwin/arm64)        RID="macos-arm64" ;;
  Linux/x86_64)        RID="linux-x64" ;;
  Linux/aarch64|Linux/arm64) RID="linux-arm64" ;;
  *) echo "install-z42: unsupported host $os/$arch" >&2
     echo "  (Windows: use install-z42.bat; supported: macos-arm64 / linux-x64 / linux-arm64)" >&2
     exit 1 ;;
esac

ASSET="z42-$VER-$RID.tar.gz"
URL="https://github.com/$SLUG/releases/download/$TAG/$ASSET"

# ── 3. staleness id (nightly: release published_at; pinned: the tag) ────────
remote_id() {
  if [ "$VER" = "nightly" ]; then
    curl -fsSL "https://api.github.com/repos/$SLUG/releases/tags/nightly" 2>/dev/null \
      | grep -m1 '"published_at"' | sed -E 's/.*"published_at": *"([^"]+)".*/\1/'
  else
    echo "$TAG"
  fi
}
ID="$(remote_id || true)"
WANT="$VER:$RID:${ID:-unknown}"
if [ -f "$STAMP" ] && [ -n "$ID" ] && [ "$(cat "$STAMP" 2>/dev/null)" = "$WANT" ]; then
  echo "install-z42: .z42 up to date ($VER / $RID)"
  exit 0
fi
# pinned + already installed (any id) → skip even if we couldn't reach the API
if [ "$VER" != "nightly" ] && [ -f "$STAMP" ] && grep -q "^$VER:$RID:" "$STAMP" 2>/dev/null; then
  echo "install-z42: $VER / $RID already installed"
  exit 0
fi

# ── 4. download → verify → extract → install ────────────────────────────────
echo "install-z42: fetching $ASSET ($TAG) → $DEST"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
curl -fSL "$URL" -o "$TMP/$ASSET" || { echo "install-z42: download failed: $URL" >&2; exit 1; }

# SHA256 verify against the release's SHA256SUMS (best-effort: skip if absent)
if curl -fsSL "https://github.com/$SLUG/releases/download/$TAG/SHA256SUMS" -o "$TMP/SHA256SUMS" 2>/dev/null; then
  line="$(grep " $ASSET\$" "$TMP/SHA256SUMS" || grep "  $ASSET\$" "$TMP/SHA256SUMS" || true)"
  if [ -n "$line" ]; then
    want_hash="${line%% *}"
    if command -v sha256sum >/dev/null 2>&1; then got_hash="$(sha256sum "$TMP/$ASSET" | cut -d' ' -f1)"
    else got_hash="$(shasum -a 256 "$TMP/$ASSET" | cut -d' ' -f1)"; fi
    [ "$want_hash" = "$got_hash" ] || { echo "install-z42: SHA256 mismatch for $ASSET" >&2; exit 1; }
    echo "install-z42: SHA256 ok"
  fi
fi

tar -xzf "$TMP/$ASSET" -C "$TMP"
INNER="$TMP/z42-$VER-$RID-release"
[ -d "$INNER" ] || INNER="$(find "$TMP" -maxdepth 1 -type d -name 'z42-*' | head -1)"
[ -d "$INNER" ] || { echo "install-z42: extracted package dir not found in $TMP" >&2; exit 1; }

rm -rf "$DEST"; mkdir -p "$DEST"
cp -R "$INNER"/. "$DEST"/
# GitHub Actions artifact upload/download strips the executable bit; restore it
# on the launcher trampoline + bundled binaries so `$DEST/z42` is runnable.
for b in "$DEST/z42" "$DEST/bin/z42" "$DEST/bin/z42vm" "$DEST/bin/z42c"; do
  [ -f "$b" ] && chmod +x "$b" 2>/dev/null || true
done
echo "$WANT" > "$STAMP"

# Entry: post launcher-at-package-root the trampoline is at .z42/z42; older
# packages had .z42/bin/z42 — report whichever exists.
ENTRY="$DEST/z42"; [ -f "$ENTRY" ] || ENTRY="$DEST/bin/z42"
echo "install-z42: installed $VER / $RID → $DEST"
echo "  entry: $ENTRY   (add '$DEST' to PATH, or run via \$REPO/.z42/z42)"
