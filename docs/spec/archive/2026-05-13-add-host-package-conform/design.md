# Design: host SDK package conforming to per-arch flat

## Architecture

```
scripts/
├── package.sh                       入口；--rid 选 RID；--profile {release,debug}
└── _lib/
    └── package_helpers.sh           pkg_emit_manifest / pkg_copy_libs /
                                     pkg_copy_native_includes / pkg_emit_examples_hello_c /
                                     pkg_sha256_check（下游 1.2–1.4 复用）

examples/embedding/                  共享 fixture root（Phase 1.0 已建；本 spec 扩充）
├── hello.z42 / hello.z42.toml       smoke fixture（add-ios-tests 已建）
├── multi_line.z42 / multi_line.z42.toml
├── hello_c/                         NEW: hello_c 源 + README 模板（package.sh 拷入每包）
│   ├── main.c                       cp from src/toolchain/host/examples/hello_c/main.c
│   └── README.md.host               host 平台特定 cc link 命令（其它平台版本由下游 1.2–1.4 出）
└── hello_rust/                      NEW: Rust 嵌入示例（仅 desktop package）
    ├── Cargo.toml
    ├── src/main.rs                  cp from src/toolchain/host/examples/hello_rust/src/main.rs
    └── README.md
```

`scripts/_lib/package_helpers.sh` 暴露的 helper：

```bash
pkg_emit_manifest <pkg_dir> <rid> <version> <profile> <build_host>
    生成 manifest.toml 五段：[package] + [contents] + [contents.native] +
    [contents.platform] (空 for desktop) + [compat]
    
pkg_copy_libs <pkg_dir>
    artifacts/build/libs/release/*.{zpkg,zsym} + index.json → <pkg_dir>/libs/

pkg_copy_native_includes <pkg_dir>
    src/runtime/include/*.h → <pkg_dir>/native/include/

pkg_emit_examples_hello_c <pkg_dir> <readme_template>
    examples/embedding/hello_c/main.c → <pkg_dir>/examples/hello_c/main.c
    z42c examples/embedding/hello.z42 -o <pkg_dir>/examples/hello_c/hello.zbc
    cp <readme_template> <pkg_dir>/examples/hello_c/README.md（platform-specific）

pkg_sha256_check <pkg_dir>
    比 <pkg_dir>/libs/* 与 artifacts/build/libs/release/* 的 SHA-256
    比 <pkg_dir>/native/include/* 与 src/runtime/include/* 的 SHA-256
    比 <pkg_dir>/examples/hello_c/main.c 与
       src/toolchain/host/examples/hello_c/main.c 的 SHA-256
    任一不一致 → exit 1
```

## Decisions

### D1: RID 命名映射 — Cargo / Rust 用 dotnet style，Package name 用 z42 约定

dotnet publish 用 `-r osx-arm64`；Cargo 用 `--target aarch64-apple-darwin`；两者都不是 z42 RID 命名（`macos-arm64`）。`detect_rid()` 输出 z42 RID；内部 helper 映射回工具链的 native 名：

```bash
# z42 RID → dotnet RID
macos-arm64 → osx-arm64
macos-x64   → osx-x64
linux-arm64 → linux-arm64
linux-x64   → linux-x64
windows-x64 → win-x64

# z42 RID → Cargo target triple
macos-arm64 → aarch64-apple-darwin
macos-x64   → x86_64-apple-darwin
linux-arm64 → aarch64-unknown-linux-gnu
linux-x64   → x86_64-unknown-linux-gnu
windows-x64 → x86_64-pc-windows-msvc
```

helper 函数：`rid_to_dotnet` / `rid_to_cargo` 各 5 行 case。

### D2: 静态库 + 动态库都显式 emit

参考 hello_c spec 的解决：`cargo rustc --release --lib --crate-type=staticlib --target <cargo-target> --manifest-path src/runtime/Cargo.toml`。emit 后 `libz42.a` 与默认 `cargo build` 产的 `libz42.rlib` coexist 在同一 target dir。

加一次 `cargo rustc --release --lib --crate-type=cdylib --target <cargo-target>` emit `libz42.{dylib,so}`。

windows-x64：cdylib 出 `z42.dll` + `z42.lib`（导入库）；staticlib 出 `z42.lib`（与 import lib 同名冲突 — 加 `--out-dir` 临时分开后 rename）。

### D3: 跨 RID 仅验 macos-arm64 ↔ macos-x64

Phase 1.1 本机验通：
- macos-arm64（host）
- macos-x64（cross-compile，需要 `rustup target add x86_64-apple-darwin`；dotnet publish `-r osx-x64` 内置支持）

linux / windows 跨 macOS compile 复杂（cross / docker / zig cc），Phase 1.1 不验，留 CI matrix。`package.sh --rid linux-x64` 输出明确 "cross-compile not supported on macOS host; use CI matrix" 错误。

### D4: SHA-256 check 集成时机

`pkg_sha256_check` 在 `package.sh` 主流程末尾跑（生成 manifest 之后）：

```bash
# package.sh 末尾
pkg_sha256_check "$PKG_DIR" || {
    echo "SHA-256 invariant check failed; package is corrupt" >&2
    exit 1
}
```

下游 1.2–1.4 实施时，每平台 build.sh 后跑同一 helper；最终 5+3+4+1=13 包 SHA 比对在 CI release 阶段做（跑一遍 `for d in artifacts/packages/z42-*; do pkg_sha256_check "$d"; done` 加跨包 SHA 比 uniq -c）。

### D5: examples/embedding/hello_c/ 作为 source-of-truth root

之前 `examples/embedding/` 只放 fixture .z42（hello.z42、multi_line.z42）。本 spec 扩它，把 `hello_c/main.c` + `hello_rust/` 也放进来，统一为"嵌入相关 source root"。

注意：`src/toolchain/host/examples/hello_c/main.c` 是 add-ios-tests / enable-hello-c-desktop 等 spec 的"目前实际 build 入口"；本 spec 把它**升级**为 `examples/embedding/hello_c/main.c`，原位置改为 path-link 或 cp。

**最少打扰路径**：`examples/embedding/hello_c/main.c` cp 自 `src/toolchain/host/examples/hello_c/main.c`；后者保留不动（不破现有 `examples/hello_c/build.sh`）。SHA-256 校确保两份持续 byte-identical（CI gate）。

下游 1.2–1.4 spec 的 build.sh 也从 `examples/embedding/hello_c/main.c` 拷出（不再从 `src/toolchain/...`），完成 source-of-truth 迁移。

### D6: package.sh 不内嵌大段 manifest 模板，转交 helper

当前 package.sh 把 manifest.toml 模板 inline 在 `cat > "$PKG_DIR/manifest.toml" <<EOF` 里。本 spec 把模板移到 `pkg_emit_manifest` helper —— 让 1.2–1.4 复用同一 schema 写 manifest。

## Cross-cutting concerns

### macos-x64 cross-compile 实操

在 Apple silicon mac 上：

```bash
# 一次性
rustup target add x86_64-apple-darwin

# 每次跑（package.sh 自动）
cargo rustc --release --lib --crate-type=staticlib \
    --target x86_64-apple-darwin \
    --manifest-path src/runtime/Cargo.toml

# 结果：
artifacts/build/runtime/x86_64-apple-darwin/release/libz42.a   ← Intel-arch staticlib
```

dotnet 端：

```bash
dotnet publish src/compiler/z42.Driver/z42.Driver.csproj \
    -c Release -r osx-x64 \
    -p:PublishSingleFile=true -p:UseAppHost=true
```

cross-compile 跨 mac arch 已经成熟（macOS 自带 Rosetta 2 + universal binaries 工具），本 spec 直接走。

### Linux / Windows cross-compile (out of scope)

Phase 1.1 不验 linux-* / windows-x64 from macOS。理由：
- linux from macOS：cargo-cross / zig cc / docker；每一种都需要 ~GB 装配
- windows from macOS：cargo-xwin / mingw-w64；类似复杂
- CI runner 各自跑 linux / windows 即可，本地不强求

`package.sh --rid linux-x64` 在 macOS 上执行时报清晰错误：
```
error: cross-compiling to linux-x64 from macos-arm64 not supported by this script.
       Run on a Linux host, or use the CI matrix:
         .github/workflows/release.yml (matrix.rid)
```

## Deferred / Future Work

### cross-compile-host-from-macos: macOS → linux / windows package 跨编

- **来源**：本 spec 草稿期 cross-cutting
- **触发原因**：方便 macOS 开发者本地产 linux / windows 包 demo
- **前置依赖**：选 cargo-cross / docker / zig cc / cargo-xwin 之一并入仓
- **触发条件**：CI matrix 之外有真实 macOS 单机 demo 需求

## Implementation Notes

### 重构 package.sh 后的结构

```
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR/.."
source "$SCRIPT_DIR/_lib/package_helpers.sh"

# Args: --rid <rid> | --profile {release,debug} | --variant <s>
parse_args "$@"
RID="${RID:-$(detect_host_rid)}"     # macos-arm64 / linux-x64 / ...
validate_rid_supported_on_host "$RID"

CARGO_TARGET=$(rid_to_cargo "$RID")
DOTNET_RID=$(rid_to_dotnet "$RID")
VERSION=$(grep -E '^version' src/runtime/Cargo.toml | head -1 | sed -E 's/.*"([^"]+)".*/\1/')

PKG_DIR="$ROOT/artifacts/packages/z42-${VERSION}-${RID}-${PROFILE}"
rm -rf "$PKG_DIR"
mkdir -p "$PKG_DIR/bin" "$PKG_DIR/libs" "$PKG_DIR/native/include" "$PKG_DIR/examples"

# 1. z42c
build_dotnet_publish "$DOTNET_RID" "$PROFILE" "$PKG_DIR/bin"

# 2. z42vm + libz42.{a,dylib,so,dll}
build_cargo_with_static_dynamic "$CARGO_TARGET" "$PROFILE" "$PKG_DIR"

# 3. libs/ + native/include/ + examples/hello_c + examples/hello_rust
pkg_copy_libs "$PKG_DIR"
pkg_copy_native_includes "$PKG_DIR"
pkg_emit_examples_hello_c "$PKG_DIR" "$ROOT/examples/embedding/hello_c/README.md.host"
pkg_emit_examples_hello_rust "$PKG_DIR"  # desktop-only

# 4. manifest.toml
pkg_emit_manifest "$PKG_DIR" "$RID" "$VERSION" "$PROFILE" "$(detect_host_rid)"

# 5. SHA invariant
pkg_sha256_check "$PKG_DIR"

echo "✅ $PKG_DIR/"
```

### `examples/embedding/hello_c/main.c` 来源

cp 自 `src/toolchain/host/examples/hello_c/main.c`（add-hello-c-desktop spec 落地的版本）；本 spec 阶段 1 完成"复制 + git add"；CI gate 保证两份持续 byte-identical（若来源更新，本份也得更新 → 一个 CI step 跑 `cmp -s`）。

## Testing Strategy

- 跑 `./scripts/package.sh release` 产 `z42-<v>-macos-arm64-release/`，pkg_sha256_check 通过
- 跑 `./scripts/package.sh release --rid macos-x64` 产 `z42-<v>-macos-x64-release/`，pkg_sha256_check 通过；`file native/libz42.a` 输出含 `x86_64`
- 用产出的 host package 跑 `examples/hello_c/main.c`（README 内 cc 命令）→ stdout = `"[host] hello, world"`
- 用产出的 host package 替 `examples/hello_rust/Cargo.toml` 的 path-dep → `cargo run` → stdout = `"hello, world"`
- `./scripts/test-all.sh` 6 stage 不退步
