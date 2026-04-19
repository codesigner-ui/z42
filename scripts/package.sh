#!/usr/bin/env bash
# scripts/package.sh — Build the z42 compiler + VM and assemble the distribution layout.
#
# Usage:
#   ./scripts/package.sh           # debug build (default)
#   ./scripts/package.sh release   # release build
#
# Output:
#   artifacts/z42/
#   ├── bin/
#   │   ├── z42c           ← compiler (dotnet publish single-file)
#   │   └── z42vm          ← VM (cargo build)
#   └── libs/
#       ├── z42.core.zpkg
#       ├── z42.io.zpkg
#       ├── z42.math.zpkg
#       ├── z42.text.zpkg
#       └── z42.collections.zpkg

set -euo pipefail

# Resolve project root regardless of working directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

PROFILE="${1:-debug}"
RUNTIME_MANIFEST="src/runtime/Cargo.toml"
COMPILER_PROJECT="src/compiler/z42.Driver/z42.Driver.csproj"
ARTIFACTS="artifacts/z42"
STDLIB_MODULES=(z42.core z42.io z42.math z42.text z42.collections)

# ── Detect runtime identifier ─────────────────────────────────────────────────
detect_rid() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"

    case "$os" in
        Darwin)
            case "$arch" in
                arm64) echo "osx-arm64" ;;
                *)     echo "osx-x64" ;;
            esac
            ;;
        Linux)
            case "$arch" in
                aarch64) echo "linux-arm64" ;;
                *)       echo "linux-x64" ;;
            esac
            ;;
        *)
            echo "win-x64" ;;
    esac
}

RID=$(detect_rid)

# ── 1. Build compiler (single-file publish) ───────────────────────────────────
echo "Publishing z42c ($PROFILE, $RID)..."
if [ "$PROFILE" = "release" ]; then
    DOTNET_CONFIG="Release"
else
    DOTNET_CONFIG="Debug"
fi

PUBLISH_TMP=$(mktemp -d)
trap 'rm -rf "$PUBLISH_TMP"' EXIT

dotnet publish "$COMPILER_PROJECT" \
    -c "$DOTNET_CONFIG" \
    -r "$RID" \
    -p:PublishSingleFile=true \
    -o "$PUBLISH_TMP" \
    --nologo -v quiet

mkdir -p "$ARTIFACTS/bin"
cp "$PUBLISH_TMP/z42c" "$ARTIFACTS/bin/z42c"
echo "  Published z42c → $ARTIFACTS/bin/z42c"

# ── 2. Build VM ───────────────────────────────────────────────────────────────
echo "Building z42vm ($PROFILE)..."
if [ "$PROFILE" = "release" ]; then
    cargo build --release --manifest-path "$RUNTIME_MANIFEST"
    VM_BIN="artifacts/rust/release/z42vm"
else
    cargo build --manifest-path "$RUNTIME_MANIFEST"
    VM_BIN="artifacts/rust/debug/z42vm"
fi

# ── 3. Create output layout ──────────────────────────────────────────────────
mkdir -p "$ARTIFACTS/bin" "$ARTIFACTS/libs"

# ── 4. Copy VM binary ────────────────────────────────────────────────────────
cp "$VM_BIN" "$ARTIFACTS/bin/z42vm"
echo "  Copied z42vm → $ARTIFACTS/bin/z42vm"

# ── 5. Stdlib placeholder files ───────────────────────────────────────────────
# libs/ only contains .zpkg files. Real .zpkg are produced by build-stdlib.sh.
echo "Populating libs/ (placeholder — run build-stdlib.sh for compiled stdlib)..."
for mod in "${STDLIB_MODULES[@]}"; do
    # Only create placeholder if the file doesn't already exist
    if [ ! -s "$ARTIFACTS/libs/${mod}.zpkg" ]; then
        touch "$ARTIFACTS/libs/${mod}.zpkg"
        echo "  ${mod}.zpkg (placeholder)"
    else
        echo "  ${mod}.zpkg (existing, kept)"
    fi
done

echo ""
echo "Done. Distribution layout at $ARTIFACTS/"
echo "  Compiler: $ARTIFACTS/bin/z42c"
echo "  VM:       $ARTIFACTS/bin/z42vm"
echo ""
echo "Next steps:"
echo "  ./scripts/build-stdlib.sh    # compile stdlib packages"
echo "  ./scripts/test-dist.sh       # run end-to-end tests with packaged binaries"
