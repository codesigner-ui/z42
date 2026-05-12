#!/usr/bin/env bash
# Run @z42/wasm playwright tests against pkg-web/ + js/.
#
# Prereq:
#   1. ./scripts/install-node-local.sh   (one-shot, installs artifacts/tools/node)
#   2. ./build.sh                        (produces pkg-web + js/stdlib + js/fixtures)
#
# This script:
#   - Prepends artifacts/tools/node/bin to PATH (for this process only)
#   - Sets PLAYWRIGHT_BROWSERS_PATH to artifacts/tools/playwright-browsers so
#     chromium download lands in-repo (~280MB; gitignored)
#   - npm install (idempotent) inside tests/
#   - npx playwright install chromium  (idempotent; first run downloads)
#   - npx playwright test
#
# Spec: docs/spec/archive/2026-05-12-add-wasm-tests/

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../../../.." && pwd)"

# ── (1) Local Node.js on PATH. ───────────────────────────────────────────

NODE_BIN_DIR="$ROOT/artifacts/tools/node/bin"
if [[ ! -x "$NODE_BIN_DIR/node" ]]; then
    echo "error: $NODE_BIN_DIR/node not found. Run ./scripts/install-node-local.sh first." >&2
    exit 1
fi
export PATH="$NODE_BIN_DIR:$PATH"

# ── (2) Playwright browser cache to artifacts/. ──────────────────────────

export PLAYWRIGHT_BROWSERS_PATH="$ROOT/artifacts/tools/playwright-browsers"

# ── (3) Tests bundle: npm install + playwright install + run. ────────────

cd "$HERE/tests"

if [[ ! -d node_modules ]]; then
    echo "npm install (one-shot)"
    npm install --no-audit --no-fund
fi

echo "playwright install chromium (idempotent — first run downloads ~280MB)"
npx playwright install chromium

echo ""
echo "npx playwright test"
echo ""
npx playwright test
