#!/usr/bin/env bash
# scripts/audit-missing-usings.sh — strict-using-resolution 2026-04-28
#
# Scan source.z42 files and add missing `using <ns>;` declarations based on
# detected stdlib type usage. Idempotent: existing usings are preserved.
#
# Detection heuristic:
#   Console / File / Directory / Path        → using Std.IO;
#   Math / Random / new Random               → using Std.Math;
#   new Queue / new Stack                    → using Std.Collections;
#   StringBuilder                            → using Std.Text;
#   TestRunner / new Test                    → using Std.Test;
#
# (List<T>, Dictionary<K,V>, KeyValuePair<K,V> live in z42.core prelude — no using.)
#
# Usage:
#   bash scripts/audit-missing-usings.sh                # patches all golden tests
#   bash scripts/audit-missing-usings.sh path/to/file   # single file

set -eu

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

# File list
if [ $# -ge 1 ]; then
    FILES="$@"
else
    FILES=$(find src/runtime/tests/golden/run -name 'source.z42' | sort)
fi

PATCHED=0
for f in $FILES; do
    [ -f "$f" ] || continue

    # Determine needed namespaces
    needed=""
    if grep -qE '\b(Console|File|Directory|Path)\.' "$f"; then
        needed="$needed Std.IO"
    fi
    if grep -qE '\b(Math|Random)\.' "$f" || grep -qE '\bnew Random\b' "$f"; then
        needed="$needed Std.Math"
    fi
    if grep -qE '\bnew (Queue|Stack)\b' "$f"; then
        needed="$needed Std.Collections"
    fi
    if grep -qE '\bStringBuilder\b' "$f"; then
        needed="$needed Std.Text"
    fi
    if grep -qE '\b(TestRunner)\b' "$f" || grep -qE '\bnew Test\b' "$f"; then
        needed="$needed Std.Test"
    fi

    [ -z "$needed" ] && continue

    # Filter out usings that already exist
    missing=""
    for ns in $needed; do
        ns_esc=$(echo "$ns" | sed 's/\./\\./g')
        if ! grep -qE "^[[:space:]]*using[[:space:]]+${ns_esc};" "$f"; then
            missing="$missing $ns"
        fi
    done
    [ -z "$missing" ] && continue

    # Insert usings: after `namespace ...;` if present, else at file top.
    tmpfile=$(mktemp)
    if grep -qE '^[[:space:]]*namespace[[:space:]]' "$f"; then
        # Append usings right after the first namespace line
        awk -v missing="$missing" '
            BEGIN { inserted = 0 }
            /^[[:space:]]*namespace[[:space:]]/ && !inserted {
                print
                n = split(missing, arr, " ")
                for (i = 1; i <= n; i++) {
                    if (arr[i] != "") print "using " arr[i] ";"
                }
                inserted = 1
                next
            }
            { print }
        ' "$f" > "$tmpfile"
    else
        # No namespace — prepend usings at top
        {
            for ns in $missing; do
                echo "using $ns;"
            done
            cat "$f"
        } > "$tmpfile"
    fi

    mv "$tmpfile" "$f"
    echo "  patched: $f (+$missing)"
    PATCHED=$((PATCHED + 1))
done

echo ""
echo "audit-missing-usings: ${PATCHED} file(s) patched"
