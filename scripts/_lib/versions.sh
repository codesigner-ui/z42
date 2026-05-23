# scripts/_lib/versions.sh — sourced helper for reading versions.toml
#
# Single source-of-truth: <repo>/versions.toml (see file head for schema).
# Each consumer (platform build.sh, setup-tools.sh) sources this lib and
# calls `versions_get` / `versions_get_list` with dotted paths.
#
# Usage (called from scripts/<x>.sh or platforms/<plat>/build.sh):
#
#   # locate this lib relative to the calling script:
#   _LIB="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_lib"  # if you sit in scripts/
#   # or:
#   _LIB="<repo>/scripts/_lib"
#   source "$_LIB/versions.sh"
#
#   ndk_version=$(versions_get build.android.ndk.version)
#   targets=$(versions_get_list platform.android.rust_targets)
#   for t in $targets; do rustup target add "$t"; done
#
# Implementation: shells out to python3 stdlib `tomllib` (3.11+; macOS 14+
# system python is fine; CI runners default to 3.12+). If you need to run
# on python <3.11, install `tomli`: `python3 -m pip install --user tomli`.
#
# Why python instead of `yq` / `dasel` / hand-grep?
#   - python3 + tomllib is stdlib on modern systems (no install)
#   - real TOML parser (no fragile sed regex)
#   - same `$(...)` call shape from bash regardless of value depth

set -euo pipefail

# Locate versions.toml: walk up from CWD until we find it (works from any
# subdir without forcing the caller to compute the path).
_versions_toml() {
    if [[ -n "${VERSIONS_TOML:-}" && -f "$VERSIONS_TOML" ]]; then
        echo "$VERSIONS_TOML"; return
    fi
    local dir
    dir="$(pwd)"
    while [[ "$dir" != "/" ]]; do
        if [[ -f "$dir/versions.toml" ]]; then
            echo "$dir/versions.toml"
            return
        fi
        dir="$(dirname "$dir")"
    done
    echo "error: versions.toml not found walking up from $(pwd)" >&2
    return 1
}

# versions_get <dotted.path>
#   Print a scalar value (string / number / bool) to stdout.
#   Returns non-zero with a clear error if the path doesn't resolve.
versions_get() {
    local path="$1"
    local toml
    toml="$(_versions_toml)"
    python3 - "$toml" "$path" <<'PY'
import sys
# Try stdlib `tomllib` (Python 3.11+); fall back to the pip `tomli`
# shim (`python3 -m pip install --user tomli`) for older Python so we
# don't force a Python upgrade just to run the build.
try:
    import tomllib
except ModuleNotFoundError:
    try:
        import tomli as tomllib
    except ModuleNotFoundError:
        sys.stderr.write(
            "versions.toml: need Python 3.11+ (stdlib tomllib) OR "
            "`python3 -m pip install --user tomli`\n")
        sys.exit(3)
toml_path, dotted = sys.argv[1], sys.argv[2]
with open(toml_path, "rb") as f:
    data = tomllib.load(f)
node = data
for key in dotted.split("."):
    if not isinstance(node, dict) or key not in node:
        sys.stderr.write(f"versions.toml: path not found: {dotted} (failed at '{key}')\n")
        sys.exit(2)
    node = node[key]
if isinstance(node, (list, dict)):
    sys.stderr.write(f"versions.toml: path {dotted!r} is a {type(node).__name__}, not a scalar (use versions_get_list)\n")
    sys.exit(2)
print(node)
PY
}

# versions_get_list <dotted.path>
#   Print a TOML array as space-separated tokens (no quoting; values must
#   be word-safe strings — which is fine for rust target triples, ABI names,
#   etc.). Use `for x in $(versions_get_list ...); do ...`.
versions_get_list() {
    local path="$1"
    local toml
    toml="$(_versions_toml)"
    python3 - "$toml" "$path" <<'PY'
import sys
try:
    import tomllib
except ModuleNotFoundError:
    try:
        import tomli as tomllib
    except ModuleNotFoundError:
        sys.stderr.write(
            "versions.toml: need Python 3.11+ (stdlib tomllib) OR "
            "`python3 -m pip install --user tomli`\n")
        sys.exit(3)
toml_path, dotted = sys.argv[1], sys.argv[2]
with open(toml_path, "rb") as f:
    data = tomllib.load(f)
node = data
for key in dotted.split("."):
    if not isinstance(node, dict) or key not in node:
        sys.stderr.write(f"versions.toml: path not found: {dotted} (failed at '{key}')\n")
        sys.exit(2)
    node = node[key]
if not isinstance(node, list):
    sys.stderr.write(f"versions.toml: path {dotted!r} is a {type(node).__name__}, not a list\n")
    sys.exit(2)
print(" ".join(str(x) for x in node))
PY
}

# versions_require_python3
#   Sanity check that python3 + tomllib are available; clear install hint if not.
versions_require_python3() {
    if ! command -v python3 >/dev/null 2>&1; then
        echo "error: python3 not found; required to parse versions.toml" >&2
        echo "       install python3.11+ (macOS: brew install python; ubuntu: apt install python3)" >&2
        return 1
    fi
    # Either stdlib tomllib (Python 3.11+) OR pip `tomli` shim works.
    if ! python3 -c "import tomllib" >/dev/null 2>&1 \
       && ! python3 -c "import tomli"   >/dev/null 2>&1; then
        echo "error: python3 lacks tomllib AND tomli — need one of:" >&2
        echo "       (a) upgrade Python to 3.11+ (macOS: brew upgrade python)" >&2
        echo "       (b) install the shim: python3 -m pip install --user tomli" >&2
        return 1
    fi
}

# ── version-verify helpers (replace rust-toolchain.toml / global.json) ────
#
# versions.toml is the SoT; these helpers used to live in rustup/dotnet
# auto-honored projection files (rust-toolchain.toml / global.json). We
# deleted those — caller scripts now invoke these checks at entry.

# versions_check_rust
#   Verify rustc/cargo channel + minimum version match versions.toml.
#   Warning-only (not fatal) — user may legitimately be on nightly to repro
#   a bug; we surface the divergence rather than block.
versions_check_rust() {
    command -v rustc >/dev/null 2>&1 || {
        echo "error: rustc not found. Install via https://rustup.rs" >&2; return 1; }
    local want_channel want_min actual
    want_channel=$(versions_get toolchain.rust.channel)
    want_min=$(versions_get toolchain.rust.min_version)
    actual=$(rustc --version 2>/dev/null | awk '{print $2}')
    # rustc --version output: "rustc 1.79.0 (xxxx 2024-xx-xx)"
    # `stable` channel matches any stable release ≥ min_version.
    if [[ -z "$actual" ]]; then
        echo "warning: cannot parse rustc version; skip check" >&2
        return 0
    fi
    # very loose semver compare (major.minor only)
    local actual_mm want_mm
    actual_mm=$(echo "$actual" | awk -F. '{printf "%d%03d", $1, $2}')
    want_mm=$(echo "$want_min" | awk -F. '{printf "%d%03d", $1, $2}')
    if (( actual_mm < want_mm )); then
        echo "warning: rustc $actual < versions.toml min $want_min ($want_channel channel)" >&2
        echo "         consider: rustup update $want_channel" >&2
    fi
}

# versions_check_dotnet
#   Verify dotnet SDK matches the pin in versions.toml.
#   Warning-only (rollForward latestFeature semantics: any same-minor SDK is OK).
versions_check_dotnet() {
    command -v dotnet >/dev/null 2>&1 || {
        echo "error: dotnet not found. Install .NET 8+ (https://dotnet.microsoft.com)." >&2; return 1; }
    local want actual want_mm actual_mm
    want=$(versions_get toolchain.dotnet.sdk)
    actual=$(dotnet --version 2>/dev/null)
    if [[ -z "$actual" ]]; then
        echo "warning: cannot read 'dotnet --version'; skip check" >&2
        return 0
    fi
    # latestFeature rollforward: same major.minor passes.
    want_mm=$(echo "$want"   | cut -d. -f1-2)
    actual_mm=$(echo "$actual" | cut -d. -f1-2)
    if [[ "$want_mm" != "$actual_mm" ]]; then
        echo "warning: dotnet SDK $actual ≠ versions.toml pin $want (same major.minor required)" >&2
        echo "         install $want: https://dotnet.microsoft.com/download" >&2
    fi
}

# versions_check_node
#   Verify node ≥ min_version. Skipped silently if node not present (only
#   wasm tests need it).
versions_check_node() {
    command -v node >/dev/null 2>&1 || return 0
    local want_min actual_major
    want_min=$(versions_get toolchain.node.min_version)
    actual_major=$(node -v 2>/dev/null | sed 's/^v//' | cut -d. -f1)
    if [[ -n "$actual_major" && "$actual_major" -lt "$want_min" ]]; then
        echo "warning: node $(node -v) < versions.toml min v$want_min" >&2
    fi
}
