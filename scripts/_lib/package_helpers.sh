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
        ios-arm64)     echo "aarch64-apple-ios" ;;
        ios-arm64-sim) echo "aarch64-apple-ios-sim" ;;
        android-arm64) echo "aarch64-linux-android" ;;
        android-armv7) echo "armv7-linux-androideabi" ;;
        android-x64)   echo "x86_64-linux-android" ;;
        android-x86)   echo "i686-linux-android" ;;
        browser-wasm)  echo "wasm32-unknown-unknown" ;;
        *) echo "error: unsupported rid '$1' (see memory: project_supported_platforms)" >&2; return 1 ;;
    esac
}

rid_to_dotnet() {
    case "$1" in
        macos-arm64)   echo "osx-arm64" ;;
        linux-arm64)   echo "linux-arm64" ;;
        linux-x64)     echo "linux-x64" ;;
        windows-x64)   echo "win-x64" ;;
        *) echo "error: unsupported rid '$1' (dotnet publish only for desktop RIDs)" >&2; return 1 ;;
    esac
}

# Returns the RID category: desktop / ios / android / wasm.
rid_category() {
    case "$1" in
        macos-*|linux-*|windows-*) echo "desktop" ;;
        ios-*)        echo "ios" ;;
        android-*)    echo "android" ;;
        browser-wasm) echo "wasm" ;;
        *) echo "unknown" ;;
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

    # Validate whitelist.
    case "$target" in
        macos-arm64|linux-arm64|linux-x64|windows-x64) ;;
        ios-arm64|ios-arm64-sim) ;;
        android-arm64|android-armv7|android-x64|android-x86) ;;
        browser-wasm) ;;
        *)
            echo "error: rid '$target' not in supported whitelist." >&2
            echo "       See memory: project_supported_platforms." >&2
            return 1
            ;;
    esac

    # Cross-host support:
    # - desktop ↔ desktop: only when target == host (no cross). Each desktop
    #   RID built on its own CI runner.
    # - macOS host can build: ios-* + browser-wasm (wasm needs only Rust target)
    # - Linux host can build: android-* + browser-wasm
    # - Windows host can build: browser-wasm
    local target_cat="$(rid_category "$target")"
    case "$host:$target_cat" in
        "$target":*)
            return 0
            ;;
        macos-*:ios)
            return 0
            ;;
        macos-*:wasm|linux-*:wasm|windows-*:wasm)
            return 0
            ;;
        linux-*:android|macos-*:android)
            return 0  # cargo-ndk handles both linux + macOS hosts
            ;;
        *)
            cat >&2 <<EOF
error: cross-compiling to '$target' (category=$target_cat) from host '$host' not supported here.
       Run on a host that natively supports '$target', or use the CI release matrix.
EOF
            return 1
            ;;
    esac
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

# Mobile / wasm placeholder bin/README.md describing future cross tools.
pkg_emit_bin_readme_placeholder() {
    local pkg_dir="$1"
    local target_label="$2"   # e.g. "iOS" / "Android" / "browser-wasm"
    local target_lower
    target_lower=$(echo "$target_label" | tr '[:upper:]' '[:lower:]')
    mkdir -p "$pkg_dir/bin"
    cat > "$pkg_dir/bin/README.md" <<EOF
# bin/

This directory is reserved for future cross-platform tools targeting
**${target_label}** —— e.g. \`z42-aotcross-${target_lower}\` (compile .z42
→ ${target_lower}-targeted .zbc) or \`z42-link-${target_lower}\`.

Mobile / wasm packages do **not** ship the host compiler (\`z42c\`); embed
prebuilt \`.zbc\` shipped from your host build pipeline.

See memory: project_mobile_no_compiler.
EOF
}

# ── iOS-specific helpers ────────────────────────────────────────────────

# Build a single-slice xcframework wrapping the just-cargo-built libz42.a.
# Usage: pkg_emit_ios_xcframework <pkg_dir> <cargo_target>
pkg_emit_ios_xcframework() {
    local pkg_dir="$1"
    local cargo_target="$2"
    local cargo_out="$_PKG_HELPERS_ROOT/artifacts/build/runtime/$cargo_target/release"
    local lib="$cargo_out/libz42.a"

    if [[ ! -f "$lib" ]]; then
        echo "error: libz42.a not built at $lib" >&2
        echo "       run: cargo rustc --release --lib --crate-type=staticlib --target $cargo_target" >&2
        return 1
    fi

    local xcf="$pkg_dir/native/Z42VM.xcframework"
    rm -rf "$xcf"
    xcodebuild -create-xcframework \
        -library "$lib" \
        -headers "$pkg_dir/native/include" \
        -output "$xcf" \
        >/dev/null
    [[ -d "$xcf" ]] || { echo "error: xcframework not created at $xcf" >&2; return 1; }
}

# Copy iOS Swift facade sources + Package.swift into the package root.
# Usage: pkg_emit_ios_facade <pkg_dir> <rid>
pkg_emit_ios_facade() {
    local pkg_dir="$1"
    local rid="$2"
    local ios_root="$_PKG_HELPERS_ROOT/src/toolchain/host/platforms/ios"

    mkdir -p "$pkg_dir/Sources/Z42VM" "$pkg_dir/Sources/Z42VMC/include"
    cp "$ios_root"/Sources/Z42VM/*.swift          "$pkg_dir/Sources/Z42VM/"
    cp "$ios_root"/Sources/Z42VMC/dummy.c         "$pkg_dir/Sources/Z42VMC/"
    cp "$ios_root"/Sources/Z42VMC/include/*       "$pkg_dir/Sources/Z42VMC/include/"

    # Emit a Package.swift that consumes the single-slice xcframework.
    cat > "$pkg_dir/Package.swift" <<'EOF'
// swift-tools-version: 5.9
// Generated by scripts/package.sh; do not edit.
// Single-slice xcframework SDK package (per docs/spec/archive/2026-05-13-define-package-layout/).

import PackageDescription

let package = Package(
    name: "Z42VM",
    platforms: [
        .iOS(.v14),
        .macOS(.v13),
    ],
    products: [
        .library(name: "Z42VM", targets: ["Z42VM"]),
    ],
    targets: [
        .target(
            name: "Z42VM",
            dependencies: ["Z42VMC", "Z42VMBinary"],
            path: "Sources/Z42VM"
        ),
        .target(
            name: "Z42VMC",
            path: "Sources/Z42VMC",
            sources: ["dummy.c"],
            publicHeadersPath: "include"
        ),
        .binaryTarget(
            name: "Z42VMBinary",
            path: "native/Z42VM.xcframework"
        ),
    ]
)
EOF
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

    local bin_list libs_list examples_list static_list dynamic_list containers_list
    bin_list=$(_glob_quoted "$pkg_dir/bin" '*')
    libs_list=$(_glob_quoted "$pkg_dir/libs" '*.zpkg')
    examples_list=$(
        if [[ -d "$pkg_dir/examples" ]]; then
            ( cd "$pkg_dir/examples" && shopt -s nullglob; \
              for d in */; do printf '"%s",' "${d%/}"; done ) | sed 's/,$//'
        fi
    )
    static_list=$(_existing_quoted "$pkg_dir/native" 'libz42.a' 'z42.lib')
    dynamic_list=$(_existing_quoted "$pkg_dir/native" 'libz42.dylib' 'libz42.so' 'z42.dll' 'z42_wasm_bg.wasm')

    # Platform containers (iOS xcframework / Android AAR / wasm pkg-* dirs).
    containers_list=$(_existing_quoted "$pkg_dir/native" 'Z42VM.xcframework')

    # Platform-specific facade section + compat fields.
    local category platform_section compat_section
    category=$(rid_category "$rid")
    case "$category" in
        ios)
            platform_section=$(cat <<PSEC
swiftpm-manifest = "Package.swift"
swift-sources    = "Sources/Z42VM"
PSEC
)
            compat_section=$(cat <<CSEC
host-min-version      = "${version}"
ios-deployment-target = "14.0"
CSEC
)
            ;;
        android)
            platform_section=$(cat <<PSEC
kotlin-sources = "kotlin/io/z42/vm"
PSEC
)
            compat_section=$(cat <<CSEC
host-min-version   = "${version}"
android-min-sdk    = 23
android-target-sdk = 34
CSEC
)
            ;;
        wasm)
            platform_section=$(cat <<PSEC
npm-manifest = "package.json"
wasm-bindgen = ["pkg-web", "pkg-nodejs"]
PSEC
)
            compat_section=$(cat <<CSEC
host-min-version     = "${version}"
wasm-bindgen-version = "0.2"
CSEC
)
            ;;
        *)
            # desktop: no platform facade
            platform_section="# desktop package: no platform-native facade (C consumers use native/include/)"
            compat_section="host-min-version = \"${version}\""
            ;;
    esac

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
containers  = [${containers_list}]
includes    = ["z42_abi.h", "z42_host.h"]

[contents.platform]
${platform_section}

[compat]
${compat_section}
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

    # 4. Swift sources cross-check (iOS packages only — skip if Sources/ absent).
    if [[ -d "$pkg_dir/Sources/Z42VM" ]]; then
        local ios_src="$_PKG_HELPERS_ROOT/src/toolchain/host/platforms/ios"
        for swift in "$pkg_dir/Sources/Z42VM/"*.swift; do
            local b
            b=$(basename "$swift")
            if ! _sha_eq "$swift" "$ios_src/Sources/Z42VM/$b"; then
                echo "  ✗ Sources/Z42VM/$b differs from platforms/ios source" >&2
                fail=1
            fi
        done
    fi

    if [[ $fail -ne 0 ]]; then
        echo "SHA-256 invariant check FAILED for $pkg_dir" >&2
        return 1
    fi
    echo "  ✓ SHA-256 invariants OK"
}
