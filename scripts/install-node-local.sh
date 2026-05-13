#!/usr/bin/env bash
# install-node-local.sh — Download Node.js (LTS) into artifacts/tools/node/
# without touching the system. Idempotent; pass --force to redownload.
#
# Used by wasm test infra (add-wasm-tests spec) and any other tooling that
# needs `node` / `npm` without polluting the user's environment.
#
# After install, invoke directly:
#   ./artifacts/tools/node/bin/node --version
#   ./artifacts/tools/node/bin/npm install ...
#
# Or temporarily prepend to PATH:
#   export PATH="$PWD/artifacts/tools/node/bin:$PATH"

set -euo pipefail

NODE_VERSION="22.11.0"   # LTS "Jod" line; latest patch as of 2026-05.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
INSTALL_DIR="$ROOT/artifacts/tools/node"

FORCE=false
for arg in "$@"; do
    case "$arg" in
        --force) FORCE=true ;;
        -h|--help)
            sed -n '2,16p' "$0" | sed 's/^# \?//'
            exit 0
            ;;
        *)
            echo "error: unknown arg: $arg" >&2
            exit 2
            ;;
    esac
done

# ── Idempotence check. ───────────────────────────────────────────────────

if [[ -x "$INSTALL_DIR/bin/node" && "$FORCE" = false ]]; then
    CURRENT=$("$INSTALL_DIR/bin/node" --version 2>/dev/null || echo unknown)
    if [[ "$CURRENT" = "v$NODE_VERSION" ]]; then
        echo "Node.js v$NODE_VERSION already installed at $INSTALL_DIR (skip; pass --force to reinstall)"
        echo "$INSTALL_DIR/bin/node"
        exit 0
    fi
    echo "existing node $CURRENT differs from target v$NODE_VERSION; reinstalling"
fi

# ── Platform detection. ──────────────────────────────────────────────────

OS=""
case "$(uname -s)" in
    Darwin) OS="darwin" ;;
    Linux)  OS="linux" ;;
    MINGW*|MSYS*|CYGWIN*)
        cat >&2 <<'EOF'
error: this installer auto-extracts the .tar.gz Node.js distribution (POSIX-only).
       On Windows, install Node.js from the official MSI installer:
         https://nodejs.org/en/download   (LTS, Windows Installer .msi)
       After install, `node --version` works from Git Bash and PowerShell.
       See docs/workflow/windows.md for the full Windows dev path.
EOF
        exit 1
        ;;
    *)      echo "error: unsupported OS $(uname -s)" >&2; exit 1 ;;
esac

ARCH=""
case "$(uname -m)" in
    arm64|aarch64) ARCH="arm64" ;;
    x86_64)        ARCH="x64" ;;
    *)             echo "error: unsupported arch $(uname -m)" >&2; exit 1 ;;
esac

NAME="node-v${NODE_VERSION}-${OS}-${ARCH}"
TARBALL="${NAME}.tar.gz"
URL="https://nodejs.org/dist/v${NODE_VERSION}/${TARBALL}"

# ── Download + extract. ──────────────────────────────────────────────────

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "fetching $URL"
curl --fail --location --silent --show-error -o "$TMP/$TARBALL" "$URL"

echo "extracting to $INSTALL_DIR"
mkdir -p "$ROOT/artifacts/tools"
rm -rf "$INSTALL_DIR"
tar -xzf "$TMP/$TARBALL" -C "$TMP"
mv "$TMP/$NAME" "$INSTALL_DIR"

# ── Verify. ──────────────────────────────────────────────────────────────

NODE_BIN="$INSTALL_DIR/bin/node"
NPM_BIN="$INSTALL_DIR/bin/npm"
[[ -x "$NODE_BIN" ]] || { echo "error: $NODE_BIN missing after extract" >&2; exit 1; }
[[ -x "$NPM_BIN"  ]] || { echo "error: $NPM_BIN missing after extract"  >&2; exit 1; }

NODE_VER=$("$NODE_BIN" --version)
# npm's shebang is `#!/usr/bin/env node` — must have node on PATH.
NPM_VER=$(PATH="$INSTALL_DIR/bin:$PATH" "$NPM_BIN" --version)

cat <<EOF

Node.js installed locally — no system PATH changes.
  node: $NODE_VER  → $NODE_BIN
  npm:  $NPM_VER  → $NPM_BIN

Usage:
  $NODE_BIN <script.js>
  PATH="$INSTALL_DIR/bin:\$PATH" npm install ...   # npm needs node on PATH

Or prepend to PATH for the current shell (covers both):
  export PATH="$INSTALL_DIR/bin:\$PATH"
EOF
