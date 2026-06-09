#!/usr/bin/env bash
# install.sh — install this z42 package into $Z42_HOME (default ~/.z42).
# install-z42-to-home (2026-06-03). Shipped at the root of a desktop package;
# run it from the unpacked package: `./install.sh`.
#
# Lays out a version-managed install (the launcher prefers this over the
# package's portable mode):
#   $Z42_HOME/bin/z42         trampoline (version-agnostic) — put on PATH
#   $Z42_HOME/bin/z42c        compiler
#   $Z42_HOME/launcher/       runtime that runs the launcher core itself
#   $Z42_HOME/runtimes/<ver>/ this version, registered via `z42 link` (a
#                             link.txt redirect to launcher/, so no second
#                             copy of z42vm/libs)
#   $Z42_HOME/config.toml     default = "<ver>"

set -euo pipefail

PKG="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
Z42_HOME="${Z42_HOME:-$HOME/.z42}"

VERSION="$(grep -m1 '^version' "$PKG/manifest.toml" | sed -E 's/.*"([^"]+)".*/\1/')"
if [ -z "$VERSION" ]; then
    echo "install: cannot read version from $PKG/manifest.toml" >&2
    exit 1
fi

vm_name="z42vm"; tramp="z42"; z42c="z42c"
[ -f "$PKG/bin/z42vm.exe" ] && { vm_name="z42vm.exe"; tramp="z42.exe"; z42c="z42c.exe"; }

echo "Installing z42 $VERSION → $Z42_HOME"
mkdir -p "$Z42_HOME/bin" "$Z42_HOME/launcher"

# 1. trampoline + compiler on PATH
# launcher-at-package-root (2026-06-04): trampoline is at the package ROOT
# ($PKG/z42), not $PKG/bin/. It still installs onto PATH at $Z42_HOME/bin/.
cp -f "$PKG/$tramp" "$Z42_HOME/bin/$tramp"
[ -f "$PKG/bin/$z42c" ] && cp -f "$PKG/bin/$z42c" "$Z42_HOME/bin/$z42c"

# 2. launcher runtime (runs the launcher core)
cp -f "$PKG/bin/$vm_name" "$Z42_HOME/launcher/$vm_name"
cp -f "$PKG/launcher.zpkg" "$Z42_HOME/launcher/launcher.zpkg"
rm -rf "$Z42_HOME/launcher/libs"
cp -R "$PKG/libs" "$Z42_HOME/launcher/libs"
# apphost stub template (add-apphost): so installed-mode `z42 apphost build`
# finds it at $Z42_HOME/launcher/apphost (alongside z42vm).
apphost_name="apphost"; [ -f "$PKG/bin/apphost.exe" ] && apphost_name="apphost.exe"
[ -f "$PKG/bin/$apphost_name" ] && { cp -f "$PKG/bin/$apphost_name" "$Z42_HOME/launcher/$apphost_name"; chmod +x "$Z42_HOME/launcher/$apphost_name" 2>/dev/null || true; }

# 3. register this version as the default app runtime (link → launcher/,
#    so z42vm/libs aren't copied twice).
Z42_HOME="$Z42_HOME" "$Z42_HOME/bin/$tramp" link "$Z42_HOME/launcher" --as "$VERSION" >/dev/null
Z42_HOME="$Z42_HOME" "$Z42_HOME/bin/$tramp" default "$VERSION" >/dev/null

echo "✓ installed z42 $VERSION"
case ":$PATH:" in
    *":$Z42_HOME/bin:"*) echo "  \$Z42_HOME/bin is already on PATH." ;;
    *) echo "  Add to PATH:  export PATH=\"$Z42_HOME/bin:\$PATH\"" ;;
esac
echo "  Then:         z42 run <app.zpkg>"
