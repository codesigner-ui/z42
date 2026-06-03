#!/usr/bin/env bash
# install-z42.command — macOS Finder double-click entry → runs install-z42.sh.
cd "$(dirname "${BASH_SOURCE[0]}")"
exec ./install-z42.sh "$@"
