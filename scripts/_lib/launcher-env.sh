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
# $artifacts/build/libraries/dist/<profile> exist.
#
# Usage:  source scripts/_lib/launcher-env.sh; setup_launcher_env "$ROOT" release

# Copy src→dst atomically: write to a unique temp in the same dir, then
# rename(2) into place. A plain `cp -f` truncates and holds the dest open for
# writing; under parallel test waves (test-all.sh --parallel) that share one
# launcher home, a concurrent wave exec'ing this same z42vm hits ETXTBSY
# ("Text file busy", os error 26) on Linux. rename(2) is atomic and never
# leaves the dest open-for-write, so exec always sees a complete, closed inode.
# (fix CI: linux-arm VM-goldens race after reorg introduced the shared home.)
_install_atomic() {
    local src="$1" dst="$2"
    local tmp="$dst.tmp.$$"
    cp -f "$src" "$tmp" && mv -f "$tmp" "$dst"
}

setup_launcher_env() {
    local root="$1"
    local profile="${2:-release}"          # which z42vm to bundle (debug|release)
    # Windows (git-bash / MSYS) binaries carry a .exe suffix; the native
    # trampoline resolves `z42vm.exe` on Windows (launcher/src/main.rs), so the
    # copied binary must keep that name. (fix CI: regen-golden Windows step.)
    local exe=""
    case "$(uname -s 2>/dev/null)" in MINGW*|MSYS*|CYGWIN*) exe=".exe";; esac
    local vmdir="$root/artifacts/build/runtime/$profile"
    # build-stdlib.sh always writes the flat stdlib view to libraries/dist/release,
    # regardless of the z42vm profile — so libs is fixed, vmdir varies.
    local libs="$root/artifacts/build/libraries/dist/release"

    # 1. native trampoline `z42` (shared cargo target → runtime/release/z42)
    cargo build --manifest-path "$root/src/toolchain/launcher/Cargo.toml" --release --quiet
    local tramp="$root/artifacts/build/runtime/release/z42$exe"

    # 2. z42 launcher core → launcher.zpkg (Exe-mode)
    Z42_LIBS="$libs" dotnet run --project "$root/src/compiler/z42.Driver" \
        --verbosity quiet --no-build -- \
        build "$root/src/toolchain/launcher/core/z42.launcher.z42.toml" --release >/dev/null

    # 3. populate the launcher runtime
    # reorg-artifacts-layout (2026-06-04): dev $Z42_HOME lives under
    # build/toolchain/launcher/home (mirrors src/toolchain/launcher); the
    # built launcher.zpkg now lands in build/toolchain/launcher (toml out_dir).
    export Z42_HOME="$root/artifacts/build/toolchain/launcher/home"
    mkdir -p "$Z42_HOME/launcher"
    _install_atomic "$vmdir/z42vm$exe" "$Z42_HOME/launcher/z42vm$exe"
    _install_atomic "$root/artifacts/build/toolchain/launcher/z42.launcher.zpkg" "$Z42_HOME/launcher/launcher.zpkg"
    # Point launcher/libs → $libs. `ln -sfn` is unlink+symlink (NOT atomic):
    # under test-all.sh --parallel, concurrent waves sharing this home race —
    # one wave's unlink opens a window where the link is briefly absent, which
    # cascades into EEXIST/'File exists' as the others' symlink() collide.
    # `mv` of a temp symlink can't fix it either (BSD/macOS mv dereferences a
    # symlink-to-dir target). Fix: create the link only when absent and NEVER
    # unlink it — every wave targets the same $libs, so a concurrent creator is
    # fine (tolerate its EEXIST). With no unlink there is no absence window, so
    # no cascade. Steady state (link already correct) short-circuits. A *wrong*
    # pre-existing link is only possible if $libs moved (can't happen within a
    # run); replace it then — that path is effectively single-threaded.
    local libslink="$Z42_HOME/launcher/libs"
    if [ "$(readlink "$libslink" 2>/dev/null)" != "$libs" ]; then
        # Remove ONLY a *wrong* existing link (re-checked here so we never rm a
        # correct one a concurrent wave just created — that would reopen the
        # absence window). In a fresh home the link is simply absent, so this rm
        # never fires and the path stays unlink-free / race-safe.
        if [ -L "$libslink" ] && [ "$(readlink "$libslink" 2>/dev/null)" != "$libs" ]; then
            rm -f "$libslink" 2>/dev/null
        fi
        ln -s "$libs" "$libslink" 2>/dev/null || true   # EEXIST from a concurrent wave is fine
        [ -L "$libslink" ] || { echo "launcher-env: failed to symlink libs" >&2; return 1; }
    fi

    # 4. the launcher dir (z42vm + libs) doubles as the default app runtime.
    # Idempotent + concurrency-safe: skip the registry writes (link.txt /
    # config.toml) once `dev` is already the default, so parallel waves don't
    # re-write them under each other.
    if [ "$("$tramp" default 2>/dev/null)" != "dev" ]; then
        "$tramp" link "$Z42_HOME/launcher" --as dev >/dev/null 2>&1 || true
        "$tramp" default dev >/dev/null 2>&1 || true
    fi

    export Z42_LAUNCHER="$tramp"
}
