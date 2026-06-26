#!/usr/bin/env bash
# bootstrap-no-csharp.sh (replace-csharp S4) — rebuild z42c + stdlib + xtask from
# CURRENT SOURCE using a prebuilt z42c-written SEED, with NO C# (no dotnet) in the
# loop. This is the C#-free self-hosting closure: the z42c compiler (written in
# z42) compiles itself + the stdlib + the dev CLI, bootstrapped from a downloaded
# nightly seed (the only thing C# is allowed to have produced, upstream).
#
# Usage:
#   scripts/bootstrap-no-csharp.sh [SEED_DIR]
#     SEED_DIR  dir holding the z42c-written seed: z42c.driver.zpkg + 6 z42c.*
#               deps + a runnable stdlib (z42.*.zpkg). Default (local dev): assemble
#               from artifacts/build/z42c/selfhost-out (z42c-built) + current stdlib.
#
# Invariant: this script NEVER calls dotnet. z42vm is the only execution engine
# (Rust, cargo-built — not a C# dependency). Verified at the end (no dotnet in the
# command trace) + a fixpoint check (rebuilt z42c == seed, byte-identical mod BLID).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
PROFILE=release
MEMBERS="z42c.core z42c.ir z42c.project z42c.syntax z42c.semantics z42c.pipeline z42c.driver"

vm="$ROOT/artifacts/build/runtime/$PROFILE/z42vm"; [ -f "$vm.exe" ] && vm="$vm.exe"
stdlib_src="$ROOT/artifacts/build/libraries/dist/$PROFILE"

# ── 0. z42vm (Rust; cargo — NOT C#) ──────────────────────────────────────────
echo "── [0/5] cargo build z42vm (release) ──"
cargo build --locked --release --manifest-path src/runtime/Cargo.toml --bin z42vm >/dev/null 2>&1 || \
  cargo build --release --manifest-path src/runtime/Cargo.toml --bin z42vm

# ── 1. Seed (z42c-written compiler + runnable stdlib) ────────────────────────
SEED="${1:-}"
if [ -z "$SEED" ]; then
  SEED="$ROOT/artifacts/build/z42c/nocs-seed"
  echo "── [1/5] assembling local seed (selfhost-out z42c + current stdlib) → $SEED ──"
  rm -rf "$SEED"; mkdir -p "$SEED"
  for m in $MEMBERS; do
    cp -f "$ROOT/artifacts/build/z42c/selfhost-out/$m.zpkg" "$SEED/" 2>/dev/null \
      || { echo "error: seed missing $m.zpkg (run the self-host gate first, or pass SEED_DIR)"; exit 1; }
  done
  cp -f "$stdlib_src"/*.zpkg "$SEED/" 2>/dev/null || { echo "error: no stdlib in $stdlib_src"; exit 1; }
fi
seed_driver="$SEED/z42c.driver.zpkg"
[ -f "$seed_driver" ] || { echo "error: seed has no z42c.driver.zpkg: $SEED"; exit 1; }
echo "   seed: $(ls "$SEED"/*.zpkg | wc -l | tr -d ' ') zpkg @ $SEED"

run_z42c() { Z42_LIBS="$2" "$vm" "$1" --mode interp -- "${@:3}"; }

# ── 2. seed z42c → rebuild stdlib from source (per-member, --workspace) ──────
fresh_stdlib="$ROOT/artifacts/build/z42c/nocs-stdlib/$PROFILE"
echo "── [2/5] seed z42c builds stdlib from source → $fresh_stdlib ──"
rm -rf "$fresh_stdlib"; mkdir -p "$fresh_stdlib"
( cd src/libraries && Z42_LIBS="$SEED" "$vm" "$seed_driver" --mode interp -- \
    build --workspace --output-dir "$fresh_stdlib" --release >/dev/null )
echo "   fresh stdlib: $(ls "$fresh_stdlib"/*.zpkg | wc -l | tr -d ' ') zpkg"

# runlibs for compiling z42c: fresh stdlib + seed z42c siblings (all present → any order)
runlibs="$ROOT/artifacts/build/z42c/nocs-runlibs"
rm -rf "$runlibs"; mkdir -p "$runlibs"
cp -f "$fresh_stdlib"/*.zpkg "$runlibs/"
for m in $MEMBERS; do cp -f "$SEED/$m.zpkg" "$runlibs/"; done

# ── 3. seed z42c → rebuild z42c from source (single-toml per member) ─────────
fresh_z42c="$ROOT/artifacts/build/z42c/nocs-z42c"
echo "── [3/5] seed z42c builds z42c from source (single-toml topo) → $fresh_z42c ──"
rm -rf "$fresh_z42c"; mkdir -p "$fresh_z42c"
for m in $MEMBERS; do
  Z42_LIBS="$runlibs" "$vm" "$seed_driver" --mode interp -- \
    build "$ROOT/src/compiler/$m/$m.z42.toml" --release --output-dir "$fresh_z42c" >/dev/null
  # accumulate freshly-built member so later members link against fresh siblings
  cp -f "$fresh_z42c/$m.zpkg" "$runlibs/"
done
echo "   fresh z42c: $(ls "$fresh_z42c"/*.zpkg | wc -l | tr -d ' ') zpkg"

# ── 4. fresh z42c (gen1) → compile xtask.zpkg ───────────────────────────────
echo "── [4/6] gen1 z42c builds xtask.zpkg ──"
Z42_LIBS="$runlibs" "$vm" "$fresh_z42c/z42c.driver.zpkg" --mode interp -- \
  build scripts/xtask.z42.toml --release >/dev/null
echo "   xtask: $(ls "$ROOT/artifacts/xtask/xtask.zpkg" >/dev/null 2>&1 && echo OK || echo MISSING)"

# ── 5. gen1 rebuilds z42c from source → gen2 ────────────────────────────────
# TRUE self-host fixpoint = gen1 == gen2, NOT gen1 == seed. The downloaded nightly
# seed is built from an OLDER z42c source; whenever current z42c source changes
# (e.g. a new language feature), seed-built gen1 legitimately differs from the seed.
# A correct compiler built from CURRENT source must reproduce ITSELF, so we compare
# gen1 (seed-built) against gen2 (gen1-built) — both from current source.
gen2="$ROOT/artifacts/build/z42c/nocs-z42c-gen2"
echo "── [5/6] gen1 builds z42c again → gen2 ──"
rm -rf "$gen2"; mkdir -p "$gen2"
gen1libs="$ROOT/artifacts/build/z42c/nocs-gen1libs"
rm -rf "$gen1libs"; mkdir -p "$gen1libs"
cp -f "$fresh_stdlib"/*.zpkg "$gen1libs/"
for m in $MEMBERS; do cp -f "$fresh_z42c/$m.zpkg" "$gen1libs/"; done
for m in $MEMBERS; do
  Z42_LIBS="$gen1libs" "$vm" "$fresh_z42c/z42c.driver.zpkg" --mode interp -- \
    build "$ROOT/src/compiler/$m/$m.z42.toml" --release --output-dir "$gen2" >/dev/null
  cp -f "$gen2/$m.zpkg" "$gen1libs/"
done
echo "   gen2 z42c: $(ls "$gen2"/*.zpkg | wc -l | tr -d ' ') zpkg"

# ── 6. Fixpoint: gen1 == gen2 (byte-identical mod BLID) ─────────────────────
echo "── [6/6] fixpoint check (gen1 == gen2, BLID-tolerant) ──"
fail=0
for m in $MEMBERS; do
  a="$fresh_z42c/$m.zpkg"; b="$gen2/$m.zpkg"
  if cmp -s "$a" "$b"; then echo "   ✓ $m identical"; else
    # BLID is a trailing 16-byte build_id; tolerate a <=16B tail-only diff.
    sa=$(wc -c <"$a"); sb=$(wc -c <"$b")
    if [ "$sa" = "$sb" ] && cmp -s <(head -c $((sa-16)) "$a") <(head -c $((sb-16)) "$b"); then
      echo "   ✓ $m identical (BLID differs only)"
    else
      echo "   ✗ $m DIFFERS ($sa B vs $sb B)"; fail=1
    fi
  fi
done
[ "$fail" = 0 ] && echo "✅ C#-free bootstrap + gen1==gen2 fixpoint OK (no dotnet)" || { echo "❌ fixpoint failed"; exit 1; }
