#!/usr/bin/env bash
# Run stdlib library [Test] tests — thin wrapper around test-lib.sh.
#
# Usage (all forms delegated to test-lib.sh):
#   ./scripts/test-stdlib.sh              # all stdlib libs
#   ./scripts/test-stdlib.sh z42.io       # specific lib
#   ./scripts/test-stdlib.sh z42.io --jobs 4   # parallel
#
# See test-lib.sh --help for the full option reference.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/test-lib.sh" "$@"
