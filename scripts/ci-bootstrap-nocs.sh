#!/usr/bin/env bash
# ci-bootstrap-nocs.sh (remove-dotnet-from-builds) — C#-free CI bootstrap into the
# STANDARD artifact locations that build-and-test's later steps (test all / package
# release / upload) expect. Replaces the C# bootstrap (`dotnet build z42.slnx` +
# `dotnet run z42.Driver -- build …`) with the staged self-host loop seeded by the
# PREVIOUS published nightly's z42c-written compiler.
#
# Chain: prev-nightly z42c seed  →  builds current xtask  →  xtask builds current
# z42c (warm self-build, standard loc) + stdlib (flat dist + index).  After this,
# `z42vm artifacts/xtask/xtask.zpkg -- test all / package release` run unchanged.
#
# Invariant: NEVER calls dotnet. z42vm (cargo-built Rust) is the only engine. The
# prev nightly is the ONLY thing C# may have produced, upstream — and the staged-
# bootstrap discipline (.claude/rules/bootstrap-seed.md) guarantees it compiles
# current source (verify with scripts/check-bootstrap-compat.sh before relying on it).
#
# Usage:  scripts/ci-bootstrap-nocs.sh [rid]
#   rid defaults to host (macos-arm64 / linux-x64 / linux-arm64 / windows-x64).
#   Needs gh (auth'd) + cargo + rust. Runs on all 4 OS build-and-test legs.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

RID="${1:-}"
if [ -z "$RID" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)        RID=macos-arm64 ;;
    Linux-x86_64)        RID=linux-x64 ;;
    Linux-aarch64)       RID=linux-arm64 ;;
    Linux-arm64)         RID=linux-arm64 ;;
    MINGW*|MSYS*|CYGWIN*) RID=windows-x64 ;;
    *) echo "::error::unsupported host for C#-free bootstrap; pass rid"; exit 2 ;;
  esac
fi

# winpath: native z42vm is a Windows binary on windows-x64 — it cannot parse the
# git-bash/MSYS path style (`/d/a/z42/z42/...`) that $PWD yields under Actions'
# shell. Any ABSOLUTE path we hand to z42vm (as an argv path or via an env var it
# reads — Z42_LIBS / Z42_PORTABLE_VM / the driver.zpkg arg) must be the native
# `D:\a\...` form. Relative paths (resolved against cwd by z42vm itself) are fine
# as-is, so only absolutes flow through here. No-op on Unix.
winpath() {
  case "$RID" in
    windows-*) cygpath -w "$1" ;;
    *)         printf '%s' "$1" ;;
  esac
}
# nightly archive: Windows ships .zip (unzip), Unix ships .tar.gz (tar).
case "$RID" in windows-*) EXT=zip ;; *) EXT=tar.gz ;; esac
MEMBERS="z42c.core z42c.ir z42c.syntax z42c.project z42c.semantics z42c.pipeline z42c.driver"
ROOT="$PWD"

# ── 0. z42vm (Rust; cargo — NOT C#) ──────────────────────────────────────────
echo "── [0/5] cargo build z42vm (release) ──"
cargo build --locked --release --manifest-path src/runtime/Cargo.toml --bin z42vm
vm="$ROOT/artifacts/build/runtime/release/z42vm"; [ -f "$vm.exe" ] && vm="$vm.exe"

# ── 1. download prev nightly → seed (z42c-written compiler + stdlib) ─────────
echo "── [1/5] download nightly seed ($RID) ──"
work="$(mktemp -d)"
# Retry the download: publish-nightly republishes the `nightly` release with a
# `gh release delete` → `gh release create` (a brief window where the release does
# NOT exist → "release not found"). Concurrent runs (run N's publish-nightly vs
# run N+1's build-and-test download) can hit that window. Retry to ride it out.
dl_ok=0
for attempt in 1 2 3 4 5 6 7 8 9 10; do
  if gh release download nightly -p "z42-runtime-nightly-${RID}.${EXT}" -O "$work/rt.${EXT}" 2>"$work/dlerr"; then
    dl_ok=1; break
  fi
  echo "   download attempt $attempt failed ($(head -1 "$work/dlerr")) — likely a publish-nightly delete→recreate window; retry in 15s…"
  rm -f "$work/rt.${EXT}"
  sleep 15
done
if [ "$dl_ok" != 1 ]; then echo "::error::nightly download failed after 10 attempts:"; cat "$work/dlerr"; exit 1; fi
mkdir -p "$work/rtpkg"
if [ "$EXT" = zip ]; then unzip -q "$work/rt.${EXT}" -d "$work/rtpkg"; else tar -C "$work/rtpkg" -xzf "$work/rt.${EXT}"; fi
if ! ls "$work/rtpkg/z42c/"*.zpkg >/dev/null 2>&1; then
  echo "::error::nightly runtime package has no z42c/ seed yet — needs a publish-nightly republish carrying the z42c-written seed (self-heals on the next run)"; exit 1
fi
seed="$ROOT/.ci-nocs-seed"; rm -rf "$seed"; mkdir -p "$seed"
cp -f "$work/rtpkg/z42c/"*.zpkg "$seed/"
cp -f "$work/rtpkg/libs/"*.zpkg "$seed/"
echo "   seed: $(ls "$seed"/*.zpkg | wc -l | tr -d ' ') zpkg @ $seed"

# Prime the STANDARD locations so xtask's warm self-build + `using Std.*` resolve:
#   * stdlib dist  ← seed stdlib (current source compiles against it; replaced by
#                    the fresh xtask-built stdlib below)
#   * z42c per-member dist  ← seed z42c (the warm seed `_buildCompilerZ42` reuses)
libs="$ROOT/artifacts/build/libraries/dist/release"; mkdir -p "$libs"
cp -f "$seed"/z42.*.zpkg "$libs/" 2>/dev/null || true
for m in $MEMBERS; do
  d="$ROOT/artifacts/build/z42c/$m/release/dist"; mkdir -p "$d"
  cp -f "$seed/$m.zpkg" "$d/"
done

# Native-form (Windows D:\… on windows-x64; unchanged on Unix) copies of every
# ABSOLUTE path z42vm must parse. The bash exec position ("$vm" …) keeps the MSYS
# form — git-bash launches the .exe fine; only what z42vm itself reads is converted.
seedw="$(winpath "$seed")"
libsw="$(winpath "$libs")"
vmw="$(winpath "$vm")"
driverw="$(winpath "$seed/z42c.driver.zpkg")"

# ── 2. seed z42c → build CURRENT xtask.zpkg ──────────────────────────────────
echo "── [2/5] seed z42c builds current xtask.zpkg ──"
Z42_LIBS="$seedw" "$vm" "$driverw" --mode interp -- \
  build scripts/xtask.z42.toml --release
[ -f "$ROOT/artifacts/xtask/xtask.zpkg" ] || { echo "::error::xtask.zpkg not produced"; exit 1; }

# ── 3. xtask builds CURRENT z42c (warm self-build → standard loc) ────────────
echo "── [3/5] xtask build compiler-z42 (warm self-build from seed) ──"
Z42_LIBS="$libsw" Z42_PORTABLE_VM="$vmw" "$vm" artifacts/xtask/xtask.zpkg -- build compiler-z42

# ── 4. xtask builds CURRENT stdlib (flat dist + index.json) ──────────────────
echo "── [4/5] xtask build stdlib ──"
Z42_LIBS="$libsw" Z42_PORTABLE_VM="$vmw" "$vm" artifacts/xtask/xtask.zpkg -- build stdlib

# ── 5. sanity: no dotnet was invoked; toolchain present ──────────────────────
echo "── [5/5] verify C#-free toolchain ──"
[ -f "$ROOT/artifacts/build/z42c/z42c.driver/release/dist/z42c.driver.zpkg" ] \
  || { echo "::error::current z42c.driver.zpkg missing after build compiler-z42"; exit 1; }
[ -f "$libs/z42.core.zpkg" ] || { echo "::error::stdlib flat dist missing"; exit 1; }
rm -rf "$work"
echo "✅ C#-free CI bootstrap OK ($RID) — z42c + stdlib + xtask in standard locations, no dotnet"
