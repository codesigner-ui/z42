# Platform: iOS — build & run requirements

> **状态**：📋 设计期。spec：[`docs/spec/changes/add-platform-ios/`](../../spec/changes/add-platform-ios/)。
> **共同前置**：[`README.md`](README.md)。
>
> 本文档先把"iOS facade 在你机器上跑起来需要什么"写清楚，让后续 spec 实施可以按这个清单走。落地完成后状态改 🟢，路径补 facade README 链接。

把 z42 VM 编进 iOS app 的 SwiftPM 包，让 Swift / SwiftUI 应用一行 `import Z42VM` 跑 `.zbc` 字节码。

## 工具链清单

| 工具 | 版本 | 用途 | 安装 |
|------|------|------|------|
| **Xcode** | 15.0+ | iOS SDK + xcodebuild + Swift toolchain | App Store 或 https://developer.apple.com/xcode/ |
| Command Line Tools | 与 Xcode 一致 | `xcrun` + `xcodebuild` | `xcode-select --install` |
| `rustup` + stable rust 1.85+ | — | Rust 编译器 | https://rustup.rs |
| `aarch64-apple-ios` target | — | 真机 ARM64 | `rustup target add aarch64-apple-ios` |
| `aarch64-apple-ios-sim` target | — | M 系列 Mac 上的 simulator | `rustup target add aarch64-apple-ios-sim` |
| `x86_64-apple-ios` target | — | Intel Mac 上的 simulator | `rustup target add x86_64-apple-ios` |
| `dotnet` 8.0+ | — | 编 z42 stdlib（共同前置）| https://dotnet.microsoft.com/download |

iOS 开发只能在 **macOS 主机**上做（Xcode 不上 Linux / Windows）。Apple Silicon Mac 推荐。

## 一次性环境准备

```bash
# 1. Xcode（一次性大下载；如已装跳过）
# 从 App Store 装最新 Xcode，启动一次接受 license
sudo xcodebuild -license accept
xcode-select --install                  # 装 CLT
xcode-select -p                         # 验证：应输出 /Applications/Xcode.app/Contents/Developer 类似路径

# 2. Rust iOS targets
rustup target add aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios

# 3. 编译器 + stdlib
dotnet build src/compiler/z42.slnx
```

## 平台 feature 与 native interop

iOS feature preset：`ios = ["interp-only", "aot", "native-interop"]`。

- `interp-only`：App Store 政策硬禁动态代码生成 → JIT 不可用
- `aot`：AOT 占位（M9 真实现；当前仅 feature 开关）
- `native-interop`：iOS app 沙箱内有 `dlopen` + libffi，可走 Tier 1 native 注册

## 构建步骤（spec 落地后启用）

```bash
cd src/toolchain/host/platforms/ios
./build.sh
```

预期 `build.sh` 内做的事：

1. fail-fast 检查 `cargo` + `xcodebuild` + 3 个 iOS target 装好
2. 编译 `Resources/<demo>.z42` → `.zbc`（如 z42c 可用）
3. 从 `artifacts/z42/libs/*.zpkg` 复制到 `Resources/stdlib/`
4. `cargo build --release --target aarch64-apple-ios --manifest-path rust/Cargo.toml`
5. `cargo build --release --target aarch64-apple-ios-sim ...`
6. `cargo build --release --target x86_64-apple-ios ...`
7. `lipo -create` 合并 simulator 两个 slice → universal sim 静态库
8. `xcodebuild -create-xcframework -library <device-arm64.a> -library <sim-universal.a> -output Z42VM.xcframework`

构建后产物：

```
src/toolchain/host/platforms/ios/
├── Z42VM.xcframework/         # SwiftPM 可消费的二进制 framework
│   ├── ios-arm64/             # 真机
│   └── ios-arm64_x86_64-simulator/   # 模拟器
└── Resources/stdlib/*.zpkg    # bundled stdlib（Xcode Copy Bundle Resources 拷进 app）
```

xcframework 与 `Resources/stdlib/*.zpkg` 都不入 git；CI 在 release 上传。

## 在 Xcode 项目里消费

App 的 `Package.swift`：

```swift
.package(path: "../path/to/z42/src/toolchain/host/platforms/ios"),
// 或一旦发到 GitHub release：
// .package(url: "https://github.com/codesigner-ui/z42-ios.git", from: "0.1.0"),
```

target 依赖：

```swift
.product(name: "Z42VM", package: "Z42VM"),
```

Swift 用法（设计期 sketch）：

```swift
import Z42VM

let vm = try Z42VM(zpkgResolver: BundleZpkgResolver())
vm.stdoutHandler = { bytes in textArea.append(String(decoding: bytes, as: UTF8.self)) }

let m = try vm.loadZbc(Data(contentsOf: zbcURL))
let e = try vm.resolveEntry(m, fqn: "App.Main")
_ = try vm.invoke(e)
```

详细 API 形态由 `add-platform-ios` spec design.md 锁定。

## 验证（spec 落地完成的 GREEN 标准）

| 检查 | 命令 |
|------|------|
| Rust 真机 build | `cargo build --release --target aarch64-apple-ios --manifest-path src/toolchain/host/platforms/ios/rust/Cargo.toml` |
| Rust simulator (M1) | `cargo build --release --target aarch64-apple-ios-sim ...` |
| Rust simulator (Intel) | `cargo build --release --target x86_64-apple-ios ...` |
| Package.swift 形态 | `swift build --package-path src/toolchain/host/platforms/ios` |
| xcframework 完整 | `./build.sh && ls Z42VM.xcframework/ios-*/` |
| 既有 lib 测试不退化 | `cargo test --manifest-path src/runtime/Cargo.toml --lib host::` ≥ 22 通过 |

## 推迟到独立 spec 的内容

- **iOSDemo Xcode 工程 + SwiftUI app**：归 `add-platform-ios-demo`
- **XCTest 套件**：归 `add-platform-ios-tests`
- **just / GitHub Actions macos-14 CI job**：归 `add-platform-ios-ci`

## 故障排查

| 现象 | 处理 |
|------|------|
| `xcodebuild: command not found` | 装 Xcode + `sudo xcode-select -s /Applications/Xcode.app` |
| `Failed to run rustc` for `aarch64-apple-ios` | `rustup target add aarch64-apple-ios`；确认 host toolchain 与 target 一致（M1 mac 不要用 x86_64 toolchain）|
| `xcodebuild -create-xcframework: simulator slice has multiple platforms` | simulator 必须先 lipo 合并；不能两个 simulator slice 都直接给 -library |
| `libffi-sys` build 失败 | iOS feature 含 `native-interop`，需要系统 libffi 跨编。设 `LIBFFI_SYS_USE_PKG_CONFIG=1` 或切换到 vendored；或临时去掉 `native-interop`（牺牲 native 注册能力） |
| `pkg-config not found for libffi` (cross-compile) | iOS cross-compile 时系统 libffi 不在 pkg-config 路径；考虑改用 libffi `bundled` feature（修改 runtime/Cargo.toml） —— 这是已知限制，平台 spec 落地时会决策 |
| `dyld: Library not loaded ...` 在 simulator | 用了 simulator slice 跑真机，或反之。检查 xcframework slice 选择 |

## 关联文档

- 设计与决策：[`docs/spec/changes/add-platform-ios/`](../../spec/changes/add-platform-ios/)
- 跨平台契约：[`platforms/README.md`](../../../src/toolchain/host/platforms/README.md)
- Embedding API：[`docs/design/runtime/embedding.md`](../../design/runtime/embedding.md)
- cross-platform.md：[`docs/design/runtime/cross-platform.md`](../../design/runtime/cross-platform.md) iOS 段
