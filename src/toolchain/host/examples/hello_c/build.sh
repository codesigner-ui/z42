#!/usr/bin/env bash
# Build and run the hello_c embedding example end-to-end:
#   1. Verify tooling (cc + cargo + dotnet).
#   2. Build runtime staticlib (libz42.a) via `cargo rustc
#      --crate-type=staticlib`; record native-static-libs line.
#   3. Ensure compiler + stdlib zpkgs exist.
#   4. Compile examples/embedding/hello.z42 → out/hello.zbc.
#   5. Compile main.c → out/hello_c, linked against libz42.a + native libs.
#   6. Run out/hello_c and assert stdout matches "[host] hello, world\n".
#
# Spec: docs/spec/archive/2026-05-12-enable-hello-c-desktop/
#
# Note on staticlib emission: `src/runtime/Cargo.toml`'s `[lib]` is rlib-only
# by default (other crates depend on it as `path = ...`). For C embedding we
# need `libz42.a`, which we get by running `cargo rustc --crate-type=staticlib`
# explicitly. Cargo coexists the rlib and staticlib artifacts in the same
# target dir — confirmed on Apple silicon macOS.

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../../../.." && pwd)"
OUT="$HERE/out"

# ── (1) Tooling check (fail-fast). ───────────────────────────────────────

for tool in cc cargo dotnet; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "error: $tool not found on PATH." >&2
        exit 1
    }
done

# ── (2) Build runtime staticlib + capture native-static-libs. ────────────

RUNTIME_MANIFEST="$ROOT/src/runtime/Cargo.toml"
RUNTIME_OUT="$ROOT/artifacts/build/runtime/release"
LIBZ42="$RUNTIME_OUT/libz42.a"
NATIVE_LIBS_FILE="$OUT/native-static-libs.txt"

mkdir -p "$OUT"

echo "cargo rustc --release --lib --crate-type=staticlib (emits libz42.a)"
# Capture the full rustc output so we can pull the native-static-libs note out.
# `--print=native-static-libs` only prints on a fresh compile; if cargo's
# cache is hot, we'll see no note. Falling back to a stored copy keeps
# repeated runs working.
RUSTC_LOG=$(cargo rustc \
    --release --lib --crate-type=staticlib \
    --manifest-path "$RUNTIME_MANIFEST" \
    -- --print=native-static-libs 2>&1) || {
    echo "$RUSTC_LOG" >&2
    echo "error: cargo rustc --crate-type=staticlib failed" >&2
    exit 1
}

# Look for: `note: native-static-libs: -liconv -lSystem -lc -lm`
NATIVE_LIBS=$(echo "$RUSTC_LOG" \
    | grep "native-static-libs:" \
    | sed "s/.*native-static-libs: //" \
    | head -1 \
    || true)

if [[ -n "$NATIVE_LIBS" ]]; then
    echo "$NATIVE_LIBS" > "$NATIVE_LIBS_FILE"
elif [[ -f "$NATIVE_LIBS_FILE" ]]; then
    NATIVE_LIBS=$(cat "$NATIVE_LIBS_FILE")
    echo "  (using cached native-static-libs from $NATIVE_LIBS_FILE)"
else
    # Final fallback for first-run-with-cache-hit; minimal macOS / Linux set.
    case "$(uname -s)" in
        Darwin) NATIVE_LIBS="-liconv -lSystem -lc -lm" ;;
        Linux)  NATIVE_LIBS="-lgcc_s -lutil -lrt -lpthread -lm -ldl -lc" ;;
        *)      NATIVE_LIBS="-lc -lm" ;;
    esac
    echo "  (rustc cache hit; falling back to platform default native libs)"
fi

[[ -f "$LIBZ42" ]] || { echo "error: $LIBZ42 missing after cargo rustc" >&2; exit 1; }
echo "  libz42.a:        $LIBZ42 ($(du -h "$LIBZ42" | cut -f1))"
echo "  native libs:     $NATIVE_LIBS"

# ── (3) Ensure compiler + stdlib. ────────────────────────────────────────

DRIVER_DLL="$ROOT/artifacts/build/compiler/z42.Driver/bin/z42c.dll"
LIBS_DIR="$ROOT/artifacts/build/libraries/dist/release"

if [[ ! -f "$DRIVER_DLL" ]]; then
    echo "building compiler…"
    dotnet build "$ROOT/src/compiler/z42.slnx"
fi
if [[ ! -d "$LIBS_DIR" ]] || ! ls "$LIBS_DIR"/*.zpkg >/dev/null 2>&1; then
    echo "error: stdlib not built at $LIBS_DIR" >&2
    echo "       build it first: z42 xtask.zpkg build stdlib" >&2
    exit 1
fi

# ── (4) Compile the fixture (.z42 → .zbc). ──────────────────────────────

ZBC_OUT="$OUT/hello.zbc"
echo "z42c examples/embedding/hello.z42 → $ZBC_OUT"
dotnet "$DRIVER_DLL" "$ROOT/examples/embedding/hello.z42" --emit zbc -o "$ZBC_OUT"

# ── (5) Compile + link main.c. ───────────────────────────────────────────

INCLUDE="$ROOT/src/runtime/include"
BIN="$OUT/hello_c"

echo "cc main.c -> $BIN"
cc -O2 -I "$INCLUDE" \
   -o "$BIN" "$HERE/main.c" \
   -L "$RUNTIME_OUT" -lz42 \
   $NATIVE_LIBS

# ── (6) Run + assert. ────────────────────────────────────────────────────

EXPECTED="[host] hello, world"
echo "running: $BIN $ZBC_OUT $LIBS_DIR"
ACTUAL=$("$BIN" "$ZBC_OUT" "$LIBS_DIR")
echo "stdout: $ACTUAL"
if [[ "$ACTUAL" != "$EXPECTED" ]]; then
    echo "FAIL: expected $(printf %q "$EXPECTED"), got $(printf %q "$ACTUAL")" >&2
    exit 1
fi
echo ""
echo "✅ hello_c end-to-end OK"
