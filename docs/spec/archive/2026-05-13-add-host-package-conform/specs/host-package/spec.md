# Spec: host SDK package (desktop) conforming to per-arch flat layout

> 落地 `define-package-layout` (1.0) 的 5 个 desktop RID package：`macos-arm64` / `macos-x64` / `linux-arm64` / `linux-x64` / `windows-x64`。Phase 1.1 本机验通 `macos-arm64` + `macos-x64`；其他 RID 留 CI 矩阵。

## ADDED Requirements

### Requirement 1: RID 命名修正

`scripts/package.sh` 产包名用 `macos-*` / `linux-*` / `windows-x64`（替代旧 `osx-*` / `win-x64` dotnet 惯例），与 `define-package-layout` D8 / Requirement 2 对齐。

#### Scenario: package.sh 在 Apple silicon macOS 产正确命名
- **WHEN** 在 macOS arm64 host 跑 `./scripts/package.sh release`
- **THEN** 产物在 `artifacts/packages/z42-<v>-macos-arm64-release/`（不是 `osx-arm64`）

#### Scenario: package.sh `--rid macos-x64` 跨 arch 出 x86_64 包
- **WHEN** 在 macOS arm64 跑 `./scripts/package.sh release --rid macos-x64`
- **THEN** 产 `artifacts/packages/z42-<v>-macos-x64-release/`，内 native/libz42.a 是 x86_64 mach-o

### Requirement 2: bin/ libs/ native/ examples/ manifest.toml 五项齐全

每个 host package 必须 5 项目齐 + 顶层无其它非约定目录。

#### Scenario: 顶层目录列出
- **WHEN** `ls artifacts/packages/z42-<v>-macos-arm64-release/`
- **THEN** 输出至少含 `bin/` + `libs/` + `native/` + `examples/` + `manifest.toml`

### Requirement 3: native/ 强制 emit 静态库 + 动态库

`scripts/package.sh` 必须显式 emit `libz42.a`（staticlib）+ `libz42.{dylib,so}`（cdylib），不依赖默认 `cargo build` 的行为。

#### Scenario: package.sh 产 libz42.a + libz42.dylib（macOS）
- **WHEN** 跑 `./scripts/package.sh release`
- **THEN** `native/libz42.a` 与 `native/libz42.dylib` 都存在，且都是 host arch（`file` 输出含 `arm64` 或 `x86_64`）

#### Scenario: native/include/ 含 z42_abi.h + z42_host.h
- **WHEN** 跑 `./scripts/package.sh release`
- **THEN** `native/include/z42_abi.h` 与 `native/include/z42_host.h` 都存在，与 `src/runtime/include/` byte-identical

### Requirement 4: examples/hello_c/ 包含 main.c + hello.zbc + README.md

`examples/hello_c/main.c` 与 `src/toolchain/host/examples/hello_c/main.c` byte-identical（即 R7 跨 13 包一致性的 source of truth）；`hello.zbc` 由 `z42c examples/embedding/hello.z42` 产；`README.md` 给 host 平台的 cc 链接命令。

#### Scenario: examples/hello_c/ 三件齐
- **WHEN** 跑 `./scripts/package.sh release`
- **THEN** `examples/hello_c/{main.c,hello.zbc,README.md}` 都存在；`main.c` 与 `src/toolchain/host/examples/hello_c/main.c` SHA-256 相同

### Requirement 5: examples/hello_rust/ desktop-only

仅 desktop package 含 `examples/hello_rust/`（Rust 嵌入示例，cp 自 `src/toolchain/host/examples/hello_rust/`，Cargo.toml path-dep 改为 README-documented 用户自填）。iOS / Android / wasm package 不含此目录。

#### Scenario: hello_rust/ 仅 desktop 出现
- **WHEN** 跑 `./scripts/package.sh release` 产 host package
- **THEN** `examples/hello_rust/{Cargo.toml,src/main.rs,README.md}` 存在

### Requirement 6: manifest.toml 完整 schema

`manifest.toml` 必须满足 `define-package-layout` R9 的字段集（`[package]` + `[contents]` + `[contents.native]` + `[contents.platform]` + `[compat]`）。

#### Scenario: manifest.toml 必填字段全
- **WHEN** 读 `artifacts/packages/z42-<v>-macos-arm64-release/manifest.toml`
- **THEN** `[package]` 含 `name="z42-macos-arm64"` + `version` + `abi-version=1` + `rid="macos-arm64"` + `profile="release"` + `build-date` + `build-host`；`[contents]` 含 `bin=["z42c","z42vm"]` + `libs=[...]` + `examples=["hello_c","hello_rust"]`；`[contents.native]` 含 `static=["libz42.a"]` + `dynamic=["libz42.dylib"]` + `containers=[]` + `includes=["z42_abi.h","z42_host.h"]`；`[contents.platform]` 段全空（desktop 无 platform-native 入口）；`[compat]` 含 `host-min-version`

### Requirement 7: SHA-256 invariant check

`scripts/_lib/package_helpers.sh` 内提供 `pkg_sha256_check` —— Phase 1.1 验单包内 `libs/*` 与 `native/include/*` 与 `examples/hello_c/main.c` 的 SHA-256 与上游 source-of-truth 一致；下游 1.2–1.4 实施后跑同 helper 验跨包 byte-identical。

#### Scenario: 单包内 source-of-truth SHA 校
- **WHEN** 包产完，跑 `pkg_sha256_check artifacts/packages/z42-<v>-macos-arm64-release/`
- **THEN** `libs/index.json` SHA 与 `artifacts/build/libs/release/index.json` 相同；`native/include/z42_abi.h` 与 `src/runtime/include/z42_abi.h` 相同；`examples/hello_c/main.c` 与 `src/toolchain/host/examples/hello_c/main.c` 相同

### Requirement 8: package.sh CLI 升级

`--rid <rid>` flag 接受 5 个枚举之一；默认值 = 当前 host RID（detect 后用新命名 `macos-*` etc.）。

#### Scenario: package.sh CLI help / 枚举
- **WHEN** `./scripts/package.sh --help`
- **THEN** 输出含 `--rid {macos-arm64,macos-x64,linux-arm64,linux-x64,windows-x64}`

## MODIFIED Requirements

### Requirement: 旧 RID 命名 `osx-*` / `win-x64` 全部替换为 D8 约定

**Before:** `package.sh detect_rid()` 用 `osx-arm64` / `osx-x64` / `linux-*` / `win-x64`（dotnet 旧 RID）；`package.sh` 内 `dotnet publish -r osx-arm64` 等

**After:** `detect_rid()` 输出 `macos-arm64` 等；`dotnet publish` 时映射回 dotnet 真实 RID（dotnet 内部仍用 `osx-*`，但 package 输出名与 manifest 都用 `macos-*`）

## Pipeline Steps

不涉及编译器 pipeline。
