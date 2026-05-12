# Spec: 跨平台 SDK package 目录约定（package-layout）

> 适用于：desktop（host）/ iOS / Android / wasm 共 13 个 per-arch package。下游 Phase 1.1–1.4 spec 实施时必须遵循本契约。

## ADDED Requirements

### Requirement 1: Package 顶层目录约定

每个 per-arch package 必须呈现以下顶层结构（绝对统一）：

```
artifacts/packages/z42-<version>-<rid>-<config>/
├── bin/                    # 平台 CLI 工具（desktop 装 z42c/z42vm；mobile/wasm 为 README 占位）
├── libs/                   # stdlib zpkg + sym + index.json (跨 package byte-identical)
├── native/                 # 平台 runtime artifacts + C ABI headers
├── examples/               # 嵌入式 demo（含 hello_c/ 跨平台 main.c byte-identical）
└── manifest.toml           # 包描述，统一 schema
```

外加各平台原生消费入口（直接放 root，无 `facade/` 层）：

| target | root 平台入口文件 |
|--------|-------|
| desktop | （无；C 用户直接用 `native/include/`）|
| iOS | `Sources/Z42VM/*.swift` + `Sources/Z42VMC/` + `Package.swift` + `native/Z42VM.xcframework`（单 slice 形态）|
| Android | `kotlin/io/z42/vm/*.kt` + `cpp/{z42vm_jni.c,CMakeLists.txt}` |
| wasm | `pkg-web/` + `pkg-nodejs/` + `js/*.js` + `package.json` |

#### Scenario: 每个 package 都有 bin / libs / native / examples / manifest.toml
- **WHEN** 比较任意两个 per-arch package 的顶层
- **THEN** 都至少含上述 5 项；平台原生消费入口在 root（不嵌套到 `facade/`）

### Requirement 2: Package 命名约定（per-arch flat）

Package 目录名格式：`z42-<version>-<rid>-<config>`。**不带 `<target>` 前缀** —— RID 已经唯一标识平台 + 架构。

**完整 RID 枚举**（Phase 1）：

| Target | RID | 说明 |
|--------|-----|------|
| Desktop (host SDK) | `macos-arm64` / `macos-x64` / `linux-arm64` / `linux-x64` / `windows-x64` | OS-arch |
| iOS device | `ios-arm64` | aarch64-apple-ios |
| iOS sim Apple silicon | `ios-arm64-sim` | aarch64-apple-ios-sim |
| iOS sim Intel | `ios-x64-sim` | x86_64-apple-ios |
| Android | `android-arm64` / `android-armv7` / `android-x64` / `android-x86` | 对应 ABI: arm64-v8a / armeabi-v7a / x86_64 / x86 |
| wasm | `wasm32` | wasm32-unknown-unknown |

合计 **13 个 release packages**（同样的 13 个 debug 形态可选）。

#### Scenario: package 目录名匹配 z42-<v>-<rid>-<config>
- **WHEN** Phase 1.1 产 macOS arm64 host package
- **THEN** 目录名 = `z42-0.1.0-macos-arm64-release`

#### Scenario: iOS 三个 slice 都产独立 package
- **WHEN** Phase 1.2 完成
- **THEN** `artifacts/packages/` 含 `z42-0.1.0-ios-arm64-release/` + `-ios-arm64-sim-release/` + `-ios-x64-sim-release/`

### Requirement 3: 跨 package libs/ 内容必须完全一致

`libs/` 内容跨 13 个 package 必须 byte-identical（zpkg 是 host-生成字节码，平台无关）：

```
libs/
├── z42.core.zpkg
├── z42.core.zsym
├── z42.io.zpkg
├── z42.io.zsym
├── z42.math.zpkg
├── z42.math.zsym
├── z42.text.zpkg
├── z42.text.zsym
├── z42.collections.zpkg
├── z42.collections.zsym
├── z42.test.zpkg
├── z42.test.zsym
└── index.json
```

#### Scenario: libs/ byte-identical across all packages
- **WHEN** 比较 13 个 package 的 `libs/` SHA-256 sum
- **THEN** 完全相同（每个文件）

### Requirement 4: 跨 package native/include/ 内容必须完全一致

`native/include/` 是 C ABI 头，跨 13 个 package 必须 byte-identical：

```
native/include/
├── z42_abi.h
└── z42_host.h
```

#### Scenario: native/include/ byte-identical
- **WHEN** 比较 13 个 package 的 native/include/ SHA-256
- **THEN** 两个 .h 完全相同（cp 自 src/runtime/include/）

### Requirement 5: native/ 每平台二进制契约

每个 per-arch package 在 `native/` 必须提供静态库 + 动态库（如平台支持）+ 平台 container（如适用）：

| RID | 静态库 (`.a`) | 动态库 / 模块 | 平台 container |
|-----|---------------|-------|----|
| **macos-{arm64,x64}** | `libz42.a` | `libz42.dylib` | — |
| **linux-{arm64,x64}** | `libz42.a` | `libz42.so` | — |
| **windows-x64** | `z42.lib` | `z42.dll` + `z42.lib`（导入库）| — |
| **ios-arm64** | `libz42.a`（device slice）| — (App Store 禁动态)| `Z42VM-ios-arm64.xcframework/`（单 slice，SwiftPM 友好）|
| **ios-arm64-sim** | `libz42.a`（sim arm64 slice）| — | `Z42VM-ios-arm64-sim.xcframework/` |
| **ios-x64-sim** | `libz42.a`（sim Intel slice）| — | `Z42VM-ios-x64-sim.xcframework/` |
| **android-{arm64,armv7,x64,x86}** | `libz42_platform_android.a` | `libz42_platform_android.so` | — (AAR 暂不进 per-arch；Phase 2 加 multi-ABI AAR 包) |
| **wasm32** | `libz42.a`（wasm32 object archive）| `z42_wasm_bg.wasm`（cdylib）| `pkg-web/` + `pkg-nodejs/` |

#### Scenario: 每 package 静态库 + 头齐
- **WHEN** Phase 1.x 产任一 per-arch package
- **THEN** `native/libz42.a`（或平台等价命名）+ `native/include/{z42_abi,z42_host}.h` 都存在

### Requirement 6: 平台原生消费入口在 root

平台语言 facade 文件（Swift / Kotlin / JS / Package.swift / package.json）**直接放 package root**，不嵌套到 `facade/`。平台原生工具（SwiftPM / Gradle / npm）一行 import 即用。

#### Scenario: iOS package SwiftPM 一行消费
- **WHEN** 用户在 Xcode 项目 `Package.swift` 写 `.package(path: ".../z42-0.1.0-ios-arm64-release")`
- **THEN** SwiftPM 能 resolve `Z42VM` library product 并 link 单 slice xcframework

#### Scenario: wasm package npm 一行消费
- **WHEN** 用户 `cd <wasm-package>/ && npm pack` 或 `npm install <path>`
- **THEN** package.json 描述的 entry 可被 import；JS API 可用

### Requirement 7: examples/hello_c/ 内容

每 package 必须含 `examples/hello_c/`（跨 package 一致的 C 嵌入参考）：

```
examples/hello_c/
├── main.c                    # 与 src/toolchain/host/examples/hello_c/main.c byte-identical
├── hello.zbc                 # build.sh 编出 examples/embedding/hello.z42 → 此文件
└── README.md                 # 平台特定 cc / xcrun / ndk-build / wasm-ld link 命令
```

`main.c` 在 13 个 package 都 byte-identical；只是 README 内 link 命令不同。

#### Scenario: examples/hello_c/main.c 跨 package byte-identical
- **WHEN** 比较 13 个 package 的 examples/hello_c/main.c SHA-256
- **THEN** 全部相同

### Requirement 8: bin/ 内容

`bin/` 在每 package 都存在。

| target | `bin/` 内容 |
|--------|-------------|
| desktop | `z42c` + `z42vm`（+ `.pdb` debug symbols 在 Windows / debug build）|
| ios / android / wasm | `README.md`（说明本目录预留 future cross tools，如 `z42-aotcross-<target>`）|

#### Scenario: desktop bin/ 含工具
- **WHEN** Phase 1.1 产 macos-arm64 package
- **THEN** `bin/z42c` 与 `bin/z42vm` 可执行

#### Scenario: mobile bin/ 仅 README
- **WHEN** Phase 1.2 产 ios-arm64 package
- **THEN** `bin/README.md` 存在；目录内无其它文件

### Requirement 9: manifest.toml 统一 schema

每 package 根含一份 `manifest.toml`：

```toml
[package]
name        = "z42-<rid>"             # z42-macos-arm64 / z42-ios-arm64 / ...
version     = "0.1.0"                 # z42 工具链版本
abi-version = 1                       # Z42_HOST_ABI_VERSION；跨所有 package 必须相同
rid         = "ios-arm64"             # 见 Requirement 2 完整枚举
profile     = "release"               # release / debug
build-date  = "2026-05-13T..."        # ISO 8601 UTC
build-host  = "macos-arm64"           # 编出本 package 的 host RID

[contents]
bin         = ["z42c", "z42vm"]       # mobile/wasm 为 []
libs        = ["z42.core.zpkg", ...]  # stdlib zpkg 列表
examples    = ["hello_c"]             # desktop 多含 ["hello_rust"]

[contents.native]
static      = ["libz42.a"]            # 平台对应静态库列表
dynamic     = ["libz42.dylib"]        # 动态库；某些 RID 为空（如 iOS）
containers  = ["Z42VM-ios-arm64.xcframework"]  # 平台 container；空数组若无
includes    = ["z42_abi.h", "z42_host.h"]

[contents.platform]
# 平台原生入口（相对 package root；空字符串若无）
swiftpm-manifest = "Package.swift"             # iOS only
swift-sources    = "Sources/Z42VM"             # iOS only
kotlin-sources   = "kotlin/io/z42/vm"          # Android only
npm-manifest     = "package.json"              # wasm only
wasm-bindgen     = ["pkg-web", "pkg-nodejs"]   # wasm only

[compat]
host-min-version          = "0.1.0"   # ABI 兼容要求
# Platform-specific 字段（仅出现在对应 package）
ios-deployment-target     = "14.0"    # iOS package
android-min-sdk           = 23         # Android package
android-target-sdk        = 34
wasm-bindgen-version      = "0.2"     # wasm package
```

#### Scenario: abi-version 跨 package 全部 = 1
- **WHEN** 读 13 个 package 的 `[package].abi-version`
- **THEN** 全部 = 1（与 Z42_HOST_ABI_VERSION 一致）

## MODIFIED Requirements

### Requirement: embedding.md §11 加 §11.9 "分发 package 形态"

**Before:** §11.7 ZpkgResolver 平台默认 / §11.8 Spec 与归档

**After:** §11.9 简介 13 个 per-arch package 目录约定 + 引向本 spec

## Pipeline Steps

不涉及编译器 pipeline。
