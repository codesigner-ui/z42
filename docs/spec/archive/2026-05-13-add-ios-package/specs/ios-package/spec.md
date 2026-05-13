# Spec: iOS per-slice SDK package

> 落地 `define-package-layout` (1.0) 的 2 个 iOS slice package（`ios-arm64` + `ios-arm64-sim`）。Phase 1 不含 `ios-x64-sim`（memory `project_supported_platforms` 白名单）。

## ADDED Requirements

### Requirement 1: iOS 2 个 per-slice package 产物

`scripts/package.sh --rid ios-arm64` 与 `--rid ios-arm64-sim` 各产一份独立 package。

#### Scenario: 两个 slice 都产独立 package
- **WHEN** `./scripts/package.sh release --rid ios-arm64 && ./scripts/package.sh release --rid ios-arm64-sim`
- **THEN** `artifacts/packages/` 含 `z42-<v>-ios-arm64-release/` + `z42-<v>-ios-arm64-sim-release/`

### Requirement 2: 每 slice package 内含完整 facade + native

每个 iOS package 顶层结构（per `define-package-layout` R1 + R6）：

```
z42-<v>-ios-<slice>-<config>/
├── bin/README.md                  占位（mobile 无 host tool）
├── libs/                          stdlib zpkg + index.json（与其它 package byte-identical）
├── native/
│   ├── libz42.a                   本 slice 静态库（cargo --target <slice> 产）
│   ├── Z42VM.xcframework/         单 slice 容器（SwiftPM 友好）
│   └── include/{z42_abi,z42_host}.h
├── Sources/Z42VM/*.swift          Swift facade（cp 自 platforms/ios/Sources/Z42VM/）
├── Sources/Z42VMC/                clang module
├── Package.swift                  SwiftPM manifest（消费本 slice）
├── examples/hello_c/{main.c,hello.zbc,README.md}
└── manifest.toml
```

#### Scenario: ios-arm64 package 含 device-arch libz42.a
- **WHEN** Phase 1.2 完成
- **THEN** `z42-<v>-ios-arm64-release/native/libz42.a` 是 aarch64-apple-ios mach-o（`file` 输出含 `arm64`）

#### Scenario: ios-arm64-sim package 含 sim-arch libz42.a
- **WHEN** Phase 1.2 完成
- **THEN** `z42-<v>-ios-arm64-sim-release/native/libz42.a` 是 aarch64-apple-ios-sim mach-o

### Requirement 3: SwiftPM 一行消费

`Package.swift` 在 package root，用户在 Xcode `.package(path: "<package-dir>")` 即可。

#### Scenario: SwiftPM resolves Z42VM product
- **WHEN** 用户在外部 Xcode 项目 `Package.swift` 加 `.package(path: "/path/to/z42-<v>-ios-arm64-release")`
- **THEN** SwiftPM 能 resolve `Z42VM` library product 并 link `native/Z42VM.xcframework`

### Requirement 4: examples/hello_c/ iOS 平台

每 iOS package 含 `examples/hello_c/{main.c,hello.zbc,README.md}`；`main.c` 跨 package byte-identical；`README.md` 给 iOS link 命令 (xcrun -sdk iphoneos / iphonesimulator)。

#### Scenario: hello_c iOS README 含 iphoneos / iphonesimulator 命令
- **WHEN** 读 `z42-<v>-ios-arm64-release/examples/hello_c/README.md`
- **THEN** 含 `xcrun -sdk iphoneos clang ... -L native -lz42 ...` 命令

### Requirement 5: SHA-256 invariant 与其它 package 一致

`libs/` + `native/include/` + `examples/hello_c/main.c` SHA-256 与 desktop / Android / wasm package 完全相同。

#### Scenario: SHA-256 check pass
- **WHEN** Phase 1.2 完成后跑 `pkg_sha256_check artifacts/packages/z42-<v>-ios-arm64-release/`
- **THEN** invariant 通过（libs / native/include / examples/hello_c/main.c 各自 SHA 与 source-of-truth 相同）

### Requirement 6: manifest.toml iOS 字段

manifest.toml `[compat]` 含 `ios-deployment-target = "14.0"`；`[contents.platform]` 含 `swiftpm-manifest = "Package.swift"` + `swift-sources = "Sources/Z42VM"`；`[contents.native].containers = ["Z42VM.xcframework"]`。

#### Scenario: iOS manifest 完整
- **WHEN** 读 `z42-<v>-ios-arm64-release/manifest.toml`
- **THEN** `[package].rid = "ios-arm64"` + `[contents.platform].swiftpm-manifest = "Package.swift"` + `[compat].ios-deployment-target = "14.0"`

## MODIFIED Requirements

### Requirement: platforms/ios/build.sh 输出位置

**Before:** `platforms/ios/build.sh` 把 xcframework 产到 `platforms/ios/Z42VM.xcframework/`（in-repo 测试用）

**After:** 仍产到 in-repo（add-ios-tests spec 依赖），**附加**导出 per-slice package 到 `artifacts/packages/z42-<v>-ios-<slice>-<config>/`；两条路径产物之间通过 hardlink / cp 共享底层 .a 文件，避免重复 build

## Pipeline Steps

不涉及编译器 pipeline。
