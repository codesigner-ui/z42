#!/usr/bin/env bash
# z42 end-to-end benchmark driver.
#
# Compiles each bench/scenarios/*.z42 → .zbc, runs hyperfine on z42vm against
# each, and merges results into bench/results/e2e.json conforming to
# bench/baseline-schema.json.
#
# Usage:
#   ./scripts/bench-run.sh           # full: warmup=3, runs=10
#   ./scripts/bench-run.sh --quick   # quick: warmup=1, runs=3, 2 scenarios only
#
# Prereqs (script auto-builds if missing):
#   - z42vm release binary (artifacts/build/runtime/release/z42vm)
#   - stdlib zpkgs (artifacts/build/libraries/dist/release/*.zpkg)

set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

QUICK=${1:-}
SCENARIOS_DIR="bench/scenarios"
RESULTS_DIR="bench/results"
mkdir -p "$RESULTS_DIR"

WARMUP=3
RUNS=10
if [[ "$QUICK" == "--quick" ]]; then
    WARMUP=1
    RUNS=3
fi

# ── Prereq: hyperfine ────────────────────────────────────────────────────
if ! command -v hyperfine >/dev/null; then
    echo "❌ hyperfine not found. Install: brew install hyperfine / cargo install hyperfine" >&2
    exit 2
fi

# ── Prereq: z42vm release build ──────────────────────────────────────────
VM="artifacts/build/runtime/release/z42vm"
if [[ ! -x "$VM" ]]; then
    echo "→ z42vm not found at $VM, building..."
    cargo build --release --manifest-path src/runtime/Cargo.toml --quiet
fi

# ── Prereq: stdlib built ─────────────────────────────────────────────────
if [[ ! -f artifacts/build/libraries/dist/release/z42.core.zpkg ]]; then
    echo "→ stdlib not found in artifacts/build/libraries/dist/release/, building..."
    ./scripts/build-stdlib.sh >/dev/null
    # build-stdlib.sh writes to artifacts/build/libraries/; package.sh copies to artifacts/build/libraries/dist/release/
    if [[ ! -f artifacts/build/libraries/dist/release/z42.core.zpkg ]]; then
        echo "→ running package.sh to populate artifacts/build/libraries/dist/release/..."
        ./scripts/package.sh >/dev/null
    fi
fi

# ── Compile scenarios → .zbc ─────────────────────────────────────────────
TMP_DIR=$(mktemp -d)
trap "rm -rf $TMP_DIR" EXIT

scenarios=("$SCENARIOS_DIR"/*.z42)
if [[ "$QUICK" == "--quick" ]]; then
    scenarios=("${scenarios[@]:0:2}")  # first 2 only
fi

echo "→ Compiling ${#scenarios[@]} scenarios..."
# Parallel arrays: scenarios[i] → entries[i] = "<namespace>.Main".
# Required because b8c97a7c dropped vm::resolve_entry's "guess Main" fallback;
# bare .zbc invocations now need an explicit positional entry. (Project builds
# via `z42c build` bake Entry into the zpkg, but bench compiles single files
# with --emit zbc and so must supply the entry itself.)
entries=()
for src in "${scenarios[@]}"; do
    name=$(basename "$src" .z42)
    out="$TMP_DIR/${name}.zbc"
    dotnet run --project src/compiler/z42.Driver -c Release -- \
        "$src" --emit zbc -o "$out" >/dev/null 2>&1
    ns=$(grep -E '^namespace ' "$src" | head -1 | sed -E 's/^namespace[[:space:]]+([^;[:space:]]+);.*/\1/')
    if [[ -z "$ns" ]]; then
        echo "❌ no 'namespace' declaration in $src" >&2
        exit 2
    fi
    entries+=("${ns}.Main")
done

# ── Run hyperfine ────────────────────────────────────────────────────────
bench_jsons=()
for i in "${!scenarios[@]}"; do
    src="${scenarios[$i]}"
    name=$(basename "$src" .z42)
    zbc="$TMP_DIR/${name}.zbc"
    entry="${entries[$i]}"
    out_json="$TMP_DIR/${name}-bench.json"
    echo "→ Benchmarking $name (entry=$entry, warmup=$WARMUP, runs=$RUNS)..."
    hyperfine \
        --warmup "$WARMUP" \
        --runs "$RUNS" \
        --export-json "$out_json" \
        --shell=none \
        --command-name "$name" \
        "$VM $zbc $entry" >/dev/null
    bench_jsons+=("$out_json")
done

# ── Merge into baseline format ───────────────────────────────────────────
COMMIT=$(git rev-parse --short HEAD 2>/dev/null || echo "unknown")
BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "unknown")
OS_TAG=$(uname -sm | tr 'A-Z ' 'a-z-')
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

OUT_FILE="$RESULTS_DIR/e2e.json"
python3 scripts/_merge-bench-results.py \
    --commit "$COMMIT" \
    --branch "$BRANCH" \
    --os "$OS_TAG" \
    --timestamp "$TIMESTAMP" \
    --output "$OUT_FILE" \
    "${bench_jsons[@]}"

echo "✅ E2E bench complete. Results: $OUT_FILE"
