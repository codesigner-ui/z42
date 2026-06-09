#!/usr/bin/env bash
#
# build-xtask.sh — produce `./xtask` at the repo root: a native apphost
# executable that runs `artifacts/xtask/xtask.zpkg` directly, so you can type
# `./xtask build package --rid macos-arm64` instead of
# `z42 artifacts/xtask/xtask.zpkg -- build package --rid macos-arm64`.
#
# The produced `./xtask` is a per-app apphost (see docs/design/runtime/launcher.md
# "apphost"): a copy of the native stub with the app's zpkg path patched in. It is
# framework-dependent — it finds the VM + stdlib (`z42vm` + `libs`) at runtime via
# the local `.z42/` runtime (or `$Z42_HOME`), it does NOT bundle them. `./xtask`
# is native + platform-specific + gitignored — regenerate it, don't commit it.
#
# Prerequisites (this script does NOT build them — they're upstream of xtask):
#   - z42vm                 : ./xtask build runtime   (cargo, → artifacts/build/runtime/release/z42vm)
#   - stdlib dist zpkgs     : ./xtask build stdlib     (→ artifacts/build/libraries/dist/release/*.zpkg)
# On a cold tree, bootstrap those first (see docs/workflow/building/stdlib.md).
#
# What this script DOES build (the three xtask-specific pieces) then patches:
#   1. apphost stub  : cargo build (launcher crate) → the native template
#   2. launcher.zpkg : dotnet driver → runs the `apphost build` patcher
#   3. xtask.zpkg    : dotnet driver → the payload the apphost runs
#
# Flags / env:
#   --no-build       skip steps 1–3, just re-patch from existing artifacts
#   Z42_LIBS / Z42_APPHOST_TEMPLATE  honored if pre-set (else derived below)

set -euo pipefail
cd "$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
ROOT="$PWD"

RUNTIME_OUT="$ROOT/artifacts/build/runtime/release"
VM="$RUNTIME_OUT/z42vm"
APPHOST_TMPL="${Z42_APPHOST_TEMPLATE:-$RUNTIME_OUT/apphost}"
LAUNCHER_ZPKG="$ROOT/artifacts/build/toolchain/launcher/z42.launcher.zpkg"
XTASK_ZPKG="$ROOT/artifacts/xtask/xtask.zpkg"
LIBS="${Z42_LIBS:-$ROOT/artifacts/build/libraries/dist/release}"
DRIVER="$ROOT/src/compiler/z42.Driver"

BUILD=1
[[ "${1:-}" == "--no-build" ]] && BUILD=0

if [[ $BUILD -eq 1 ]]; then
  echo "[1/3] apphost stub (cargo, launcher crate)"
  cargo build --release --manifest-path "$ROOT/src/toolchain/launcher/Cargo.toml"

  echo "[2/3] launcher.zpkg (dotnet driver)"
  dotnet run --project "$DRIVER" -- build "$ROOT/src/toolchain/launcher/core/z42.launcher.z42.toml" --release

  echo "[3/3] xtask.zpkg (dotnet driver)"
  dotnet run --project "$DRIVER" -- build "$ROOT/scripts/xtask.z42.toml" --release
fi

# Prereq checks — these come from `./xtask build runtime` / `build stdlib`.
[[ -x "$VM" ]]               || { echo "error: z42vm not built at $VM — run: ./xtask build runtime" >&2; exit 1; }
[[ -e "$APPHOST_TMPL" ]]     || { echo "error: apphost stub not built at $APPHOST_TMPL — run without --no-build" >&2; exit 1; }
[[ -e "$LAUNCHER_ZPKG" ]]    || { echo "error: launcher.zpkg not built at $LAUNCHER_ZPKG — run without --no-build" >&2; exit 1; }
[[ -e "$XTASK_ZPKG" ]]       || { echo "error: xtask.zpkg not built at $XTASK_ZPKG — run without --no-build" >&2; exit 1; }
ls "$LIBS"/*.zpkg >/dev/null 2>&1 || { echo "error: stdlib dist not built at $LIBS — run: ./xtask build stdlib" >&2; exit 1; }

# Patch the stub → ./xtask. Run the `apphost build` patcher (z42 code in
# launcher.zpkg) via z42vm directly; Z42_APPHOST_TEMPLATE points it at the fresh
# stub, Z42_LIBS lets z42vm resolve the launcher's own stdlib deps. `--out xtask`
# embeds the zpkg path RELATIVE to ./xtask's dir (the repo root) → relocatable.
echo "[patch] artifacts/xtask/xtask.zpkg → ./xtask"
Z42_LIBS="$LIBS" Z42_APPHOST_TEMPLATE="$APPHOST_TMPL" \
  "$VM" "$LAUNCHER_ZPKG" -- apphost build "$XTASK_ZPKG" --out xtask

echo "done — run ./xtask  (e.g. ./xtask build package --rid macos-arm64)"
