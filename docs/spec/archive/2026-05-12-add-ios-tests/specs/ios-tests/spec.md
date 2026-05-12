# Spec: iOS facade XCTest 实现（ios-tests）

> 实现 [`platform-test-contract`](../../../../archive/2026-05-12-define-platform-test-contract/specs/platform-test-contract/spec.md) R1–R7。本 spec 只描述 iOS / macOS 平台落地形态；scenario 语义见 contract spec。

## 平台执行环境

- **运行**：macOS host `swift test`（首落地版本只支持 arm64-only macOS，详见 design.md D1）
- **link**：`Z42VM.xcframework` 的 `macos-arm64` slice（build.sh 新增）
- **xcframework slice 集合**：`ios-arm64` + `ios-arm64_x86_64-simulator` + `macos-arm64`（首版）

`xcodebuild test -destination 'platform=iOS Simulator'` 留给后续 CI spec。

## scenario → XCTest 方法名映射

| 契约 # | XCTest 方法 | 说明 |
|--------|-------------|------|
| R1 | `testSmokeHelloWorld()` | load `hello.zbc` → invoke `Hello.Main` → stdout sink == `"hello, world\n"` |
| R2 | `testBadZbcThrowsBadZbc()` | `loadZbc(Data([0xDE, 0xAD, 0xBE, 0xEF]))` → `Z42VMError.badZbc` (status 10) |
| R3 | `testResolveUnknownEntryThrowsEntryNotFound()` | load hello.zbc 后 `resolveEntry("App.Ghost")` → `entryNotFound` (status 20) |
| R4 | `testInvokeWrongArgCountThrowsArgMismatch()` | `Hello.Main` 接受 0 个参数；传 1 个 → `argMismatch` (status 21) |
| R5 | `testMapResolverWithoutCorelibSurfacesAtInvoke()` | 构造 VM 装 `MapZpkgResolver(["Std.Phantom": Data()])` → loadZbc 或 invoke 抛 `badZbc` / `vmException`，消息含 stdlib namespace |
| R6 | `testInitShutdownLifecycleRoundtrip()` | 闭包内构造 + 自动 `deinit` × 3 轮，每轮跑 R1 smoke |
| R7 | `testMultiLineStdoutPreservesOrder()` | load `multi_line.zbc` → invoke → 字节累积 == `"a\nb\nc\n"` |

## ADDED Requirements

### Requirement: XCTest target wired with xcframework resources

#### Scenario: swift test resolves Z42VMTests resources
- **WHEN** 仓库 clean clone 后 `cd src/toolchain/host/platforms/ios && ./build.sh && swift test`
- **THEN** `swift test` 执行成功，Z42VMTests target 能从 `Bundle.module/test-fixtures/` 找到 `hello.zbc` 与 `multi_line.zbc`，能从 `Bundle.module/stdlib/` 找到 `z42.core.zpkg` 等 stdlib

### Requirement: build.sh produces macos slice + test fixtures

#### Scenario: build.sh 产 xcframework + test fixtures
- **WHEN** `./build.sh` 执行
- **THEN** 产物路径含：
  - `Z42VM.xcframework/ios-arm64/`
  - `Z42VM.xcframework/ios-arm64_x86_64-simulator/`
  - `Z42VM.xcframework/macos-arm64/`
  - `Resources/stdlib/{z42.core,z42.io,z42.math,z42.text,z42.collections,z42.test}.zpkg`
  - `Resources/test-fixtures/hello.zbc`
  - `Resources/test-fixtures/multi_line.zbc`

### Requirement: build.sh 引用最新 stdlib 路径

#### Scenario: build.sh references artifacts/build/libs/release
- **WHEN** 读 build.sh
- **THEN** `LIBS_DIR` 变量指向 `artifacts/build/libs/release`（旧 `artifacts/z42/libs` 已替换）

## MODIFIED Requirements

### Requirement: iOS Limitations 段移除 "Demo / XCTest / CI: 推迟"

**Before:** `README.md` 限制段落含 `**Demo / XCTest / CI**：推迟到独立 spec（add-platform-ios-demo / -tests / -ci）`

**After:** 该行只保留 `**Demo / CI**: 推迟到独立 spec（add-platform-ios-demo / -ci）`；XCTest 在本 spec 落地

## Pipeline Steps

不涉及编译器 pipeline（pure facade testing）。
