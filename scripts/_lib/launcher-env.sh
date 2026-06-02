#!/usr/bin/env bash
# scripts/_lib/launcher-env.sh — set up a dev `z42` launcher environment so
# ported scripts can run as Exe-zpkgs via the launcher instead of
# `z42c run <script.z42>` (add-z42-launcher cutover, 2026-06-03).
#
# Builds the native trampoline + the z42 launcher core, populates
# $Z42_HOME/launcher/{z42vm, launcher.zpkg, libs}, and links it as the
# default "dev" app runtime. Exports:
#   Z42_LAUNCHER  — path to the `z42` trampoline binary
#   Z42_HOME      — the dev launcher home (under artifacts/)
#
# Assumes the caller already built z42vm (cargo --<profile>) and stdlib
# (build-stdlib.sh) — i.e. $artifacts/build/runtime/<profile>/z42vm and
# $artifacts/build/libs/<profile> exist.
#
# Usage:  source scripts/_lib/launcher-env.sh; setup_launcher_env "$ROOT" release

setup_launcher_env() {
    local root="$1"
    local profile="${2:-release}"
    local vmdir="$root/artifacts/build/runtime/$profile"
    local libs="$root/artifacts/build/libs/$profile"

    # 1. native trampoline `z42` (shared cargo target → runtime/release/z42)
    cargo build --manifest-path "$root/src/toolchain/launcher/Cargo.toml" --release --quiet
    local tramp="$root/artifacts/build/runtime/release/z42"

    # 2. z42 launcher core → launcher.zpkg (Exe-mode)
    Z42_LIBS="$libs" dotnet run --project "$root/src/compiler/z42.Driver" \
        --verbosity quiet --no-build -- \
        build "$root/src/toolchain/launcher/core/z42.launcher.z42.toml" --release >/dev/null

    # 3. populate the launcher runtime
    export Z42_HOME="$root/artifacts/launcher-home"
    mkdir -p "$Z42_HOME/launcher"
    cp -f "$vmdir/z42vm" "$Z42_HOME/launcher/z42vm"
    cp -f "$root/src/toolchain/launcher/core/dist/z42.launcher.zpkg" "$Z42_HOME/launcher/launcher.zpkg"
    ln -sfn "$libs" "$Z42_HOME/launcher/libs"

    # 4. the launcher dir (z42vm + libs) doubles as the default app runtime
    "$tramp" link "$Z42_HOME/launcher" --as dev >/dev/null
    "$tramp" default dev >/dev/null

    export Z42_LAUNCHER="$tramp"
}
