#!/usr/bin/env bash
# check-bootstrap-compat.sh — staged-bootstrap boundary check.
#
# Enforces the staged-bootstrap discipline (.claude/rules/bootstrap-seed.md):
# the LAST PUBLISHED nightly's z42c must be able to compile the CURRENT repo
# source. If it can't, current source is using syntax / a zbc·zpkg format newer
# than the published nightly supports — a boundary violation that would break
# the C#-free bootstrap (build-and-test downloads the previous nightly to seed).
#
# It dual-compiles the z42c workspace and reports:
#   (A) nightly z42c  → current source   ← the boundary check (must PASS)
#   (B) repo  z42c    → current source   ← the normal self-build (sanity)
# Both OK ⇒ no boundary issue. (A) fails but (B) passes ⇒ you used something
# newer than the published nightly: split the change into "support first
# (compile-able by the old nightly), use later" per the discipline.
#
# Usage:  scripts/check-bootstrap-compat.sh [rid]
#   rid defaults to the host (macos-arm64 / linux-x64 / …). Needs `gh` (auth'd)
#   to download the nightly, cargo+rust to build the repo z42vm, and a built
#   repo z42c (artifacts/build/z42c/.../release/dist) for leg (B) — run
#   `z42 xtask.zpkg build compiler-z42` first if absent.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

RID="${1:-}"
if [ -z "$RID" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)  RID=macos-arm64 ;;
    Darwin-x86_64) RID=macos-x64 ;;
    Linux-x86_64)  RID=linux-x64 ;;
    Linux-aarch64) RID=linux-arm64 ;;
    *) echo "unknown host; pass rid explicitly (e.g. macos-arm64)"; exit 2 ;;
  esac
fi
MEMBERS="z42c.core z42c.ir z42c.syntax z42c.project z42c.semantics z42c.pipeline z42c.driver"

# ── fetch the latest nightly (z42vm + libs/ + z42c/ seed) ──────────────────────
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
echo "→ downloading nightly runtime package ($RID) …"
case "$RID" in windows-*) ext=zip ;; *) ext=tar.gz ;; esac
gh release download nightly -p "z42-runtime-nightly-${RID}.${ext}" -O "$work/rt.$ext"
mkdir -p "$work/nightly"
if [ "$ext" = zip ]; then unzip -q "$work/rt.$ext" -d "$work/nightly"; else tar -C "$work/nightly" -xzf "$work/rt.$ext"; fi
NVM="$work/nightly/z42vm"; [ -f "$NVM.exe" ] && NVM="$NVM.exe"; chmod +x "$NVM" 2>/dev/null || true
if ! ls "$work/nightly/z42c/"*.zpkg >/dev/null 2>&1; then
  echo "::error:: nightly package has no z42c/ seed — needs a publish-nightly run carrying the z42c seed"; exit 1
fi

run_workspace() {  # $1=label  $2=vm  $3=driver.zpkg  $4=stdlib-dir
  local label="$1" vm="$2" drv="$3" std="$4" rl out m rc fail=0
  rl="$(mktemp -d)"; cp "$std"/*.zpkg "$rl"/ 2>/dev/null || true
  cp "$(dirname "$drv")"/*.zpkg "$rl"/ 2>/dev/null || true
  out="$(mktemp -d)"
  echo "── [$label] compiling current z42c workspace ──"
  for m in $MEMBERS; do
    if Z42_LIBS="$rl" "$vm" "$drv" --mode interp -- build "src/z42c/$m/$m.z42.toml" \
         --release --output-dir "$out/$m" >"$work/$label.$m.log" 2>&1 && [ -s "$out/$m/$m.zpkg" ]; then
      echo "   ✓ $m"; cp "$out/$m/$m.zpkg" "$rl"/
    else
      echo "   ✗ $m FAILED"; tail -6 "$work/$label.$m.log" | sed 's/^/        /'; fail=1
    fi
  done
  return $fail
}

# ── (A) nightly z42c → current source (the boundary check) ────────────────────
A=0
run_workspace "nightly" "$NVM" "$work/nightly/z42c/z42c.driver.zpkg" "$work/nightly/libs" || A=1

# ── (B) repo z42c → current source (normal self-build) ────────────────────────
B=0
REPO_DRV="artifacts/build/z42c/z42c.driver/release/dist/z42c.driver.zpkg"
REPO_VM="artifacts/build/runtime/release/z42vm"; [ -f "$REPO_VM.exe" ] && REPO_VM="$REPO_VM.exe"
REPO_STD="artifacts/build/libraries/dist/release"
if [ -f "$REPO_DRV" ] && [ -f "$REPO_VM" ]; then
  # colocate repo z42c siblings into one dir (per-member dist isn't colocated)
  rdh="$(mktemp -d)"; for m in $MEMBERS; do cp "artifacts/build/z42c/$m/release/dist/$m.zpkg" "$rdh"/ 2>/dev/null || true; done
  run_workspace "repo" "$PWD/$REPO_VM" "$rdh/z42c.driver.zpkg" "$PWD/$REPO_STD" || B=1
else
  echo "── [repo] skipped: repo z42c not built (run \`z42 xtask.zpkg build compiler-z42\` first) ──"; B=2
fi

# ── verdict ───────────────────────────────────────────────────────────────────
echo ""
if [ $A -eq 0 ]; then
  echo "✅ nightly z42c compiles current source — NO staged-bootstrap boundary violation"
else
  echo "❌ BOUNDARY VIOLATION: the published nightly z42c CANNOT compile current source."
  echo "   Current source uses syntax / zbc·zpkg format newer than the last nightly."
  echo "   Fix per bootstrap-seed.md: land the SUPPORT (old-syntax-written) first, publish a"
  echo "   nightly, THEN use the new syntax. Or revert the premature use."
fi
[ $B -eq 0 ] && echo "✅ repo z42c self-build OK" || { [ $B -eq 2 ] && echo "ℹ repo self-build skipped (z42c not built)"; }
exit $A
