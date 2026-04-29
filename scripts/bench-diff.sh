#!/usr/bin/env bash
# Compare current bench results against a baseline JSON.
# Both files must conform to bench/baseline-schema.json.
#
# Usage:
#   ./scripts/bench-diff.sh --current <path> --baseline <path>
#                          [--threshold-time 0.05] [--threshold-memory 0.10]
#                          [--quiet]
#
# Defaults:
#   --current   bench/results/e2e.json
#   --baseline  bench/baselines/main-<os>.json (auto-detected via uname)
#   --threshold-time   0.05  (5% slower triggers fail)
#   --threshold-memory 0.10  (10% larger triggers fail)
#
# Exit codes:
#   0  no regression
#   1  regression detected (above threshold)
#   2  tool error (missing files, malformed JSON, etc.)
#
# Output:
#   Per-benchmark diff line:
#     <name>  <metric>  <baseline>  →  <current>  ↑/↓/≈ <pct>%
#   Improvement (↓ time / smaller memory) is annotated but never fails.

set -euo pipefail

CURRENT="bench/results/e2e.json"
BASELINE=""
THRESHOLD_TIME="0.05"
THRESHOLD_MEMORY="0.10"
QUIET=0

while [[ $# -gt 0 ]]; do
    case $1 in
        --current)            CURRENT="$2"; shift 2 ;;
        --baseline)           BASELINE="$2"; shift 2 ;;
        --threshold-time)     THRESHOLD_TIME="$2"; shift 2 ;;
        --threshold-memory)   THRESHOLD_MEMORY="$2"; shift 2 ;;
        --quiet)              QUIET=1; shift ;;
        -h|--help)
            sed -n '2,/^$/p' "$0" | sed 's/^# \?//' | head -25
            exit 0
            ;;
        *)
            echo "error: unknown arg $1" >&2
            exit 2
            ;;
    esac
done

# ── Validate jq ──────────────────────────────────────────────────────────
if ! command -v jq >/dev/null; then
    echo "error: jq not installed (brew install jq / apt install jq)" >&2
    exit 2
fi

# ── Auto-detect baseline if not specified ────────────────────────────────
if [[ -z "$BASELINE" ]]; then
    OS_TAG=$(uname -sm | tr 'A-Z ' 'a-z-')
    BASELINE="bench/baselines/main-${OS_TAG}.json"
fi

# ── Validate inputs ──────────────────────────────────────────────────────
if [[ ! -f "$CURRENT" ]]; then
    echo "error: current results not found: $CURRENT" >&2
    echo "  hint: run \`just bench-e2e\` first" >&2
    exit 2
fi
if [[ ! -f "$BASELINE" ]]; then
    echo "error: baseline not found: $BASELINE" >&2
    echo "  hint: P1.D.2 will publish baselines to gh-pages on push-to-main." >&2
    echo "  hint: for local testing, copy current to baseline:" >&2
    echo "        cp '$CURRENT' '$BASELINE'" >&2
    exit 2
fi

# ── Validate JSON shape ──────────────────────────────────────────────────
for f in "$CURRENT" "$BASELINE"; do
    if ! jq -e '.schema_version == 1 and .benchmarks' "$f" >/dev/null 2>&1; then
        echo "error: $f does not match baseline-schema.json (need schema_version=1, benchmarks[])" >&2
        exit 2
    fi
done

# ── Diff per benchmark ───────────────────────────────────────────────────
regression_count=0
total_count=0

if [[ "$QUIET" == "0" ]]; then
    cur_meta=$(jq -r '"\(.commit) on \(.os)"' "$CURRENT")
    base_meta=$(jq -r '"\(.commit) on \(.os)"' "$BASELINE")
    echo "Comparing:"
    echo "  current:  $cur_meta  ($CURRENT)"
    echo "  baseline: $base_meta ($BASELINE)"
    echo "  thresholds: time>${THRESHOLD_TIME}=fail, memory>${THRESHOLD_MEMORY}=fail"
    echo ""
fi

# Build a tab-separated table of (name, metric, baseline_value, current_value)
# from the union of names across both files. Missing entries are skipped with
# a "(new)" / "(removed)" annotation.
diff_table=$(jq -r --slurpfile base "$BASELINE" '
    . as $cur |
    ($cur.benchmarks | map({ key: "\(.name)|\(.metric)", value: . })) as $cur_idx |
    ($base[0].benchmarks | map({ key: "\(.name)|\(.metric)", value: . })) as $base_idx |
    ([$cur_idx[].key, $base_idx[].key] | unique) as $keys |
    $keys[] as $k |
    {
        cur:  ($cur_idx  | map(select(.key == $k)) | .[0].value),
        base: ($base_idx | map(select(.key == $k)) | .[0].value)
    } |
    {
        # Prefer current.name; fall back to baseline.name; both should match.
        name:   (.cur.name   // .base.name   // "?"),
        metric: (.cur.metric // .base.metric // "?"),
        base_v: (.base.value // "null"),
        cur_v:  (.cur.value  // "null"),
        unit:   (.base.unit  // .cur.unit  // "?")
    } |
    [.name, .metric, .base_v, .cur_v, .unit] | @tsv
' "$CURRENT")

# bash + awk to compute deltas and check thresholds
result=$(echo "$diff_table" | awk -F'\t' \
    -v th_time="$THRESHOLD_TIME" \
    -v th_mem="$THRESHOLD_MEMORY" \
    -v quiet="$QUIET" '
{
    name   = $1
    metric = $2
    base   = $3
    cur    = $4
    unit   = $5
    total++

    label = sprintf("%s [%s]", name, metric)

    if (base == "null") {
        if (!quiet) printf "  %-40s (new)         %.3f %s\n", label, cur+0, unit
        next
    }
    if (cur == "null") {
        if (!quiet) printf "  %-40s (removed)     %.3f %s\n", label, base+0, unit
        next
    }

    base_n = base + 0
    cur_n  = cur + 0
    if (base_n == 0) {
        if (!quiet) printf "  %-40s baseline=0 (skip)\n", label
        next
    }
    delta = (cur_n - base_n) / base_n

    is_memory = (metric == "memory" || unit == "bytes" || unit == "KB" || unit == "MB")
    threshold = is_memory ? th_mem+0 : th_time+0

    if (delta > threshold) {
        sym = "↑"  # regression (slower / bigger)
        regressions++
    } else if (delta < -threshold) {
        sym = "↓"  # improvement
    } else {
        sym = "≈"  # within noise
    }

    if (!quiet || delta > threshold) {
        printf "  %-40s %.3f %s → %.3f %s  %s %+.1f%%\n", \
            label, base_n, unit, cur_n, unit, sym, delta*100
    }
}
END {
    printf "%d|%d\n", regressions+0, total
}')

stats_line=$(echo "$result" | tail -1)
diff_lines=$(echo "$result" | sed '$d')
[[ -n "$diff_lines" ]] && echo "$diff_lines"

regression_count="${stats_line%%|*}"
total_count="${stats_line##*|}"

echo ""
if [[ "$regression_count" -gt 0 ]]; then
    echo "❌ $regression_count regression(s) above threshold (out of $total_count benchmarks)"
    exit 1
else
    echo "✅ no regression above threshold ($total_count benchmarks compared)"
    exit 0
fi
