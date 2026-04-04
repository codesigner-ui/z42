#!/usr/bin/env bash
# scripts/package.sh — Build the z42 VM and assemble the distribution layout.
#
# Usage:
#   ./scripts/package.sh           # debug build (default)
#   ./scripts/package.sh release   # release build
#
# Output:
#   artifacts/z42/
#   ├── bin/z42vm
#   └── libs/
#       ├── z42.core.zbc        (placeholder until M7 build-stdlib)
#       ├── z42.core.zpkg       (placeholder)
#       ├── z42.io.zbc
#       ├── z42.io.zpkg
#       ├── z42.math.zbc
#       ├── z42.math.zpkg
#       ├── z42.text.zbc
#       ├── z42.text.zpkg
#       ├── z42.collections.zbc
#       └── z42.collections.zpkg

set -euo pipefail

PROFILE="${1:-debug}"
RUNTIME_MANIFEST="src/runtime/Cargo.toml"
ARTIFACTS="artifacts/z42"
STDLIB_MODULES=(z42.core z42.io z42.math z42.text z42.collections)

# ── 1. Build VM ────────────────────────────────────────────────────────────────
echo "Building z42vm ($PROFILE)..."
if [ "$PROFILE" = "release" ]; then
    cargo build --release --manifest-path "$RUNTIME_MANIFEST"
    VM_BIN="artifacts/rust/release/z42vm"
else
    cargo build --manifest-path "$RUNTIME_MANIFEST"
    VM_BIN="artifacts/rust/debug/z42vm"
fi

# ── 2. Create output layout ────────────────────────────────────────────────────
mkdir -p "$ARTIFACTS/bin" "$ARTIFACTS/libs"

# ── 3. Copy VM binary ──────────────────────────────────────────────────────────
cp "$VM_BIN" "$ARTIFACTS/bin/z42vm"
echo "  Copied z42vm → $ARTIFACTS/bin/z42vm"

# ── 4. Stdlib placeholder files ────────────────────────────────────────────────
# Real .zbc/.zpkg are produced by build-stdlib.sh once [Native] is supported (M7).
echo "Populating libs/ (placeholder — M7 will replace with compiled stdlib)..."
for mod in "${STDLIB_MODULES[@]}"; do
    touch "$ARTIFACTS/libs/${mod}.zbc"
    touch "$ARTIFACTS/libs/${mod}.zpkg"
    echo "  ${mod}.zbc  ${mod}.zpkg"
done

echo ""
echo "Done. Distribution layout at $ARTIFACTS/"
echo "  Run: $ARTIFACTS/bin/z42vm --verbose <file.z42ir.json>"
