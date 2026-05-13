# package_helpers.sh — Shared helpers for scripts/package.sh + platform
# build.sh scripts (Phase 1 SDK packaging).
#
# Convention: source this file from another bash script:
#     source "$ROOT/scripts/_lib/package_helpers.sh"
#
# Exposes:
#   detect_host_rid                — echo current host's z42 RID
#   rid_to_cargo <rid>             — echo Cargo target triple
#   rid_to_dotnet <rid>            — echo dotnet publish RID
#   validate_rid_supported_on_host <rid>
#   pkg_copy_libs <pkg_dir>
#   pkg_copy_native_includes <pkg_dir>
#   pkg_emit_examples_hello_c <pkg_dir> <readme_src>
#   pkg_emit_examples_hello_rust <pkg_dir>
#   pkg_emit_manifest <pkg_dir> <rid> <version> <profile> <build_host>
#   pkg_sha256_check <pkg_dir>
#
# Spec: docs/spec/changes/add-host-package-conform/specs/host-package/spec.md
#       docs/spec/archive/2026-05-13-define-package-layout/specs/package-layout/spec.md

# Resolve repo root regardless of caller's cwd.
_PKG_HELPERS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
_PKG_HELPERS_ROOT="$(cd "$_PKG_HELPERS_DIR/../.." && pwd)"

# ── RID detection + mapping ──────────────────────────────────────────────

# Supported RID whitelist —— see memory: project_supported_platforms.
# macOS Intel (macos-x64) 退场（Apple 转 Apple silicon）；iOS x86_64 sim
# 同步退场（依赖 Intel Mac host）。
#
# Supported:
#   Desktop:  macos-arm64 / linux-arm64 / linux-x64 / windows-x64
#   iOS:      ios-arm64 / ios-arm64-sim
#   Android:  android-arm64 / android-armv7 / android-x64 / android-x86
#   wasm:     wasm32

detect_host_rid() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"
    case "$os" in
        Darwin)
            case "$arch" in
                arm64)  echo "macos-arm64" ;;
                x86_64) echo "macos-x64-unsupported" ;;  # 触发 validate 报错
                *)      echo "macos-arm64" ;;
            esac ;;
        Linux)
            case "$arch" in
                aarch64) echo "linux-arm64" ;;
                x86_64)  echo "linux-x64" ;;
                *)       echo "linux-x64" ;;
            esac ;;
        MINGW*|MSYS*|CYGWIN*) echo "windows-x64" ;;
        *) echo "windows-x64" ;;
    esac
}

rid_to_cargo() {
    case "$1" in
        macos-arm64)   echo "aarch64-apple-darwin" ;;
        linux-arm64)   echo "aarch64-unknown-linux-gnu" ;;
        linux-x64)     echo "x86_64-unknown-linux-gnu" ;;
        windows-x64)   echo "x86_64-pc-windows-msvc" ;;
        *) echo "error: unsupported rid '$1' (see memory: project_supported_platforms)" >&2; return 1 ;;
    esac
}

rid_to_dotnet() {
    case "$1" in
        macos-arm64)   echo "osx-arm64" ;;
        linux-arm64)   echo "linux-arm64" ;;
        linux-x64)     echo "linux-x64" ;;
        windows-x64)   echo "win-x64" ;;
        *) echo "error: unsupported rid '$1'" >&2; return 1 ;;
    esac
}

# Returns 0 if rid is in the whitelist AND can be built on the current
# host; returns 1 + prints error otherwise.
validate_rid_supported_on_host() {
    local target="$1"
    local host
    host="$(detect_host_rid)"

    if [[ "$host" == "macos-x64-unsupported" ]]; then
        cat >&2 <<EOF
error: macOS Intel (x86_64) hosts are not in z42's supported matrix.
       Use Apple silicon (arm64) Mac, or run on Linux / Windows.
       See memory: project_supported_platforms.
EOF
        return 1
    fi

    case "$target" in
        macos-arm64|linux-arm64|linux-x64|windows-x64) ;;
        *)
            echo "error: rid '$target' not in supported whitelist." >&2
            echo "       Supported desktop RIDs: macos-arm64 / linux-arm64 / linux-x64 / windows-x64" >&2
            return 1
            ;;
    esac

    if [[ "$target" == "$host" ]]; then
        return 0
    fi
    # Phase 1: no desktop cross-compile from macOS host (only native-arch).
    # CI matrix runs each runner natively.
    cat >&2 <<EOF
error: cross-compiling to '$target' from host '$host' not supported by this script.
       Run on a host of '$target', or use the CI release matrix.
EOF
    return 1
}

# ── Package content helpers ──────────────────────────────────────────────

pkg_copy_libs() {
    local pkg_dir="$1"
    local libs_dir="$_PKG_HELPERS_ROOT/artifacts/build/libs/release"
    if [[ ! -d "$libs_dir" ]]; then
        echo "error: stdlib not built at $libs_dir; run ./scripts/build-stdlib.sh" >&2
        return 1
    fi
    mkdir -p "$pkg_dir/libs"
    cp "$libs_dir"/*.zpkg "$pkg_dir/libs/" 2>/dev/null || true
    cp "$libs_dir"/*.zsym "$pkg_dir/libs/" 2>/dev/null || true
    if [[ -f "$libs_dir/index.json" ]]; then
        cp "$libs_dir/index.json" "$pkg_dir/libs/index.json"
    else
        echo "error: $libs_dir/index.json missing" >&2
        return 1
    fi
}

pkg_copy_native_includes() {
    local pkg_dir="$1"
    mkdir -p "$pkg_dir/native/include"
    cp "$_PKG_HELPERS_ROOT/src/runtime/include/z42_abi.h" "$pkg_dir/native/include/"
    cp "$_PKG_HELPERS_ROOT/src/runtime/include/z42_host.h" "$pkg_dir/native/include/"
}

pkg_emit_examples_hello_c() {
    local pkg_dir="$1"
    local readme_src="$2"   # absolute path to platform-specific README template
    local dst="$pkg_dir/examples/hello_c"
    mkdir -p "$dst"
    cp "$_PKG_HELPERS_ROOT/examples/embedding/hello_c/main.c" "$dst/main.c"
    cp "$readme_src" "$dst/README.md"

    # Compile hello.z42 → hello.zbc using host z42c (must be built).
    local driver="$_PKG_HELPERS_ROOT/artifacts/build/compiler/z42.Driver/bin/z42c.dll"
    if [[ ! -f "$driver" ]]; then
        echo "error: z42c not built at $driver; run dotnet build src/compiler/z42.slnx" >&2
        return 1
    fi
    dotnet "$driver" \
        "$_PKG_HELPERS_ROOT/examples/embedding/hello.z42" \
        --emit zbc \
        -o "$dst/hello.zbc" >/dev/null
}

# Desktop-only: emit examples/hello_rust/.
pkg_emit_examples_hello_rust() {
    local pkg_dir="$1"
    local src="$_PKG_HELPERS_ROOT/examples/embedding/hello_rust"
    local dst="$pkg_dir/examples/hello_rust"
    mkdir -p "$dst/src"
    cp "$src/Cargo.toml" "$dst/Cargo.toml"
    cp "$src/src/main.rs" "$dst/src/main.rs"
    cp "$src/README.md"   "$dst/README.md"
}

pkg_emit_manifest() {
    local pkg_dir="$1"
    local rid="$2"
    local version="$3"
    local profile="$4"
    local build_host="$5"
    local build_date
    build_date="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

    # Comma-quoted list of names that actually exist in <dir>.
    _existing_quoted() {
        local dir="$1"; shift
        if [[ ! -d "$dir" ]]; then return; fi
        local name out=""
        for name in "$@"; do
            [[ -e "$dir/$name" ]] && out="${out}\"${name}\","
        done
        printf '%s' "${out%,}"
    }

    # Comma-quoted list of all entries matching a glob in <dir>.
    _glob_quoted() {
        local dir="$1" pattern="$2"
        if [[ ! -d "$dir" ]]; then return; fi
        local out="" name
        ( shopt -s nullglob; cd "$dir" && for name in $pattern; do
              printf '"%s",' "$name"
          done ) | sed 's/,$//'
    }

    local bin_list libs_list examples_list static_list dynamic_list
    bin_list=$(_glob_quoted "$pkg_dir/bin" '*')
    libs_list=$(_glob_quoted "$pkg_dir/libs" '*.zpkg')
    examples_list=$(
        if [[ -d "$pkg_dir/examples" ]]; then
            ( cd "$pkg_dir/examples" && shopt -s nullglob; \
              for d in */; do printf '"%s",' "${d%/}"; done ) | sed 's/,$//'
        fi
    )
    static_list=$(_existing_quoted "$pkg_dir/native" 'libz42.a' 'z42.lib')
    dynamic_list=$(_existing_quoted "$pkg_dir/native" 'libz42.dylib' 'libz42.so' 'z42.dll')

    cat > "$pkg_dir/manifest.toml" <<EOF
[package]
name        = "z42-${rid}"
version     = "${version}"
abi-version = 1
rid         = "${rid}"
profile     = "${profile}"
build-date  = "${build_date}"
build-host  = "${build_host}"

[contents]
bin         = [${bin_list}]
libs        = [${libs_list}]
examples    = [${examples_list}]

[contents.native]
static      = [${static_list}]
dynamic     = [${dynamic_list}]
containers  = []
includes    = ["z42_abi.h", "z42_host.h"]

[contents.platform]
# desktop package: no platform-native facade (C consumers use native/include/)

[compat]
host-min-version = "${version}"
EOF
}

# Verify byte-identical invariants against source-of-truth files in the
# repo. Exits non-zero on mismatch.
pkg_sha256_check() {
    local pkg_dir="$1"
    local fail=0

    # Helper: compare two files via SHA-256. macOS / Linux compatible.
    _sha_eq() {
        local a="$1" b="$2"
        local sa sb
        if command -v shasum >/dev/null 2>&1; then
            sa=$(shasum -a 256 "$a" 2>/dev/null | awk '{print $1}')
            sb=$(shasum -a 256 "$b" 2>/dev/null | awk '{print $1}')
        else
            sa=$(sha256sum "$a" 2>/dev/null | awk '{print $1}')
            sb=$(sha256sum "$b" 2>/dev/null | awk '{print $1}')
        fi
        [[ -n "$sa" && "$sa" == "$sb" ]]
    }

    # 1. libs/ files vs artifacts/build/libs/release/
    local libs_src="$_PKG_HELPERS_ROOT/artifacts/build/libs/release"
    for f in "$pkg_dir/libs/"*; do
        local b
        b=$(basename "$f")
        if [[ -f "$libs_src/$b" ]]; then
            if ! _sha_eq "$f" "$libs_src/$b"; then
                echo "  ✗ libs/$b differs from $libs_src/$b" >&2
                fail=1
            fi
        fi
    done

    # 2. native/include/ vs src/runtime/include/
    for h in z42_abi.h z42_host.h; do
        if ! _sha_eq "$pkg_dir/native/include/$h" \
                    "$_PKG_HELPERS_ROOT/src/runtime/include/$h"; then
            echo "  ✗ native/include/$h differs from src/runtime/include/$h" >&2
            fail=1
        fi
    done

    # 3. examples/hello_c/main.c vs examples/embedding/hello_c/main.c
    if ! _sha_eq "$pkg_dir/examples/hello_c/main.c" \
                "$_PKG_HELPERS_ROOT/examples/embedding/hello_c/main.c"; then
        echo "  ✗ examples/hello_c/main.c differs from examples/embedding/hello_c/main.c" >&2
        fail=1
    fi

    if [[ $fail -ne 0 ]]; then
        echo "SHA-256 invariant check FAILED for $pkg_dir" >&2
        return 1
    fi
    echo "  ✓ SHA-256 invariants OK (libs / native/include / examples/hello_c/main.c)"
}
