#!/usr/bin/env bash
# scripts/test-cross-zpkg.sh — Run multi-zpkg end-to-end tests.
#
# Each test directory is `src/tests/cross-zpkg/<name>/` containing:
#   target/    — z42.toml + src/*.z42 (lib, no deps)
#   ext/       — z42.toml + src/*.z42 (lib, depends on target — and other deps)
#   main/      — z42.toml + src/Main.z42 (exe, depends on target + ext)
#   expected_output.txt
#
# Build order: target → ext → main. zpkgs from previous steps are placed in
# `<test>/libs/` so subsequent builds find them via PackageCompiler's libs lookup.
# The `main` zpkg/zbc is then run with z42vm and stdout compared to expected.
#
# Usage:
#   ./scripts/test-cross-zpkg.sh                 # interp mode
#   ./scripts/test-cross-zpkg.sh jit             # jit mode
#
# Exit code: 0 if all tests pass, 1 otherwise.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
cd "$ROOT"

MODE="${1:-interp}"
TESTS_DIR="src/tests/cross-zpkg"
DRIVER_DLL="artifacts/compiler/z42.Driver/bin/z42c.dll"
RUNTIME_MANIFEST="src/runtime/Cargo.toml"

# Ensure compiler + VM are built. Run noisily on first failure but suppress
# normal up-to-date output.
echo "Building compiler + VM..."
dotnet build src/compiler/z42.slnx >/tmp/test-cross-zpkg-build.log 2>&1 || {
    cat /tmp/test-cross-zpkg-build.log; exit 1; }
cargo build -q --manifest-path "$RUNTIME_MANIFEST"
echo ""

# Locate stdlib zpkgs (workspace stdlib output, C4c+).
# 子目录布局：artifacts/libraries/<lib>/dist/<lib>.zpkg
# 不再依赖 artifacts/z42/libs（那个由 package.sh 分发版打包时填充）。
STDLIB_ROOT="$ROOT/artifacts/libraries"
# 检查至少一个 <lib>/dist/<lib>.zpkg 存在
if ! ls "$STDLIB_ROOT"/*/dist/*.zpkg >/dev/null 2>&1; then
    echo "Building stdlib (required for cross-zpkg tests)..."
    "$SCRIPT_DIR/build-stdlib.sh" >/dev/null
    echo ""
fi

PASS=0
FAIL=0
FAILURES=()

build_one_pkg() {
    local pkg_dir="$1"
    local pkg_libs="$2"   # dir to populate this pkg's deps via symlinks
    # Populate libs dir with previously-built deps.
    rm -rf "$pkg_dir/libs"
    mkdir -p "$pkg_dir/libs"
    if [ -d "$pkg_libs" ]; then
        for zpkg in "$pkg_libs"/*.zpkg; do
            [ -f "$zpkg" ] || continue
            cp "$zpkg" "$pkg_dir/libs/"
        done
    fi
    # Find the toml.
    local toml
    toml=$(ls "$pkg_dir"/*.toml 2>/dev/null | head -1)
    if [ -z "$toml" ]; then
        echo "    error: no toml in $pkg_dir"
        return 1
    fi
    # Build (writes to <pkg_dir>/dist/<name>.zpkg).
    if ! dotnet "$DRIVER_DLL" build "$toml" >/dev/null 2>&1; then
        echo "    build failed: $pkg_dir"
        dotnet "$DRIVER_DLL" build "$toml" 2>&1 | sed 's/^/      /' | head -10
        return 1
    fi
    return 0
}

for dir in "$TESTS_DIR"/*/; do
    name=$(basename "$dir")
    expected="$dir/expected_output.txt"
    [ -f "$expected" ] || continue

    echo "── Test: $name ──"

    # Stage 1: build target (no inter-test deps)
    if ! build_one_pkg "$dir/target" ""; then
        FAIL=$((FAIL + 1)); FAILURES+=("$name (target build)"); continue
    fi

    # Stage 2: build ext (deps on target)
    if ! build_one_pkg "$dir/ext" "$dir/target/dist"; then
        FAIL=$((FAIL + 1)); FAILURES+=("$name (ext build)"); continue
    fi

    # Stage 3: build main (deps on target + ext)
    rm -rf "$dir/main/libs"
    mkdir -p "$dir/main/libs"
    cp "$dir/target/dist"/*.zpkg "$dir/main/libs/" 2>/dev/null || true
    cp "$dir/ext/dist"/*.zpkg    "$dir/main/libs/" 2>/dev/null || true
    main_toml=$(ls "$dir/main"/*.toml 2>/dev/null | head -1)
    if ! dotnet "$DRIVER_DLL" build "$main_toml" >/dev/null 2>&1; then
        echo "    main build failed"
        dotnet "$DRIVER_DLL" build "$main_toml" 2>&1 | sed 's/^/      /' | head -10
        FAIL=$((FAIL + 1)); FAILURES+=("$name (main build)"); continue
    fi

    # Stage 4: run main zpkg with z42vm.
    # VM resolves a single libs_dir; assemble a per-test libs dir containing
    # stdlib zpkgs + our two test packages so lazy_loader can find everything.
    main_zpkg=$(ls "$dir/main/dist"/*.zpkg 2>/dev/null | head -1)
    if [ -z "$main_zpkg" ]; then
        echo "    main produced no zpkg"
        FAIL=$((FAIL + 1)); FAILURES+=("$name (no zpkg)"); continue
    fi
    test_libs=$(mktemp -d)
    # 收集 stdlib：每个 <lib>/dist/<lib>.zpkg 都要拷
    cp "$STDLIB_ROOT"/*/dist/*.zpkg "$test_libs/" 2>/dev/null || true
    cp "$dir/target/dist"/*.zpkg "$test_libs/" 2>/dev/null || true
    cp "$dir/ext/dist"/*.zpkg    "$test_libs/" 2>/dev/null || true
    actual=$(Z42_LIBS="$test_libs" cargo run -q --manifest-path "$RUNTIME_MANIFEST" -- "$main_zpkg" --mode "$MODE" 2>&1) || true
    rm -rf "$test_libs"

    if [ "$actual" = "$(cat "$expected")" ]; then
        PASS=$((PASS + 1))
        echo "  PASS"
    else
        FAIL=$((FAIL + 1))
        FAILURES+=("$name")
        echo "  FAIL"
        echo "    expected: $(head -3 "$expected" | tr '\n' '|')"
        echo "    actual:   $(echo "$actual" | head -3 | tr '\n' '|')"
    fi
done

echo ""
echo "══════════════════════════════════════"
echo " Total: $PASS passed, $FAIL failed"
echo "══════════════════════════════════════"

if [ "$FAIL" -gt 0 ]; then
    echo ""
    echo "Failed tests:"
    for f in "${FAILURES[@]}"; do
        echo "  - $f"
    done
fi

[ "$FAIL" -eq 0 ]
