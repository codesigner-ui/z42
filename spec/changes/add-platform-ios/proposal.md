# Proposal: Add iOS Platform Scaffold

## Why

z42 当前**无 iOS 支持**。iOS 是与 Android 并列的移动端必备平台。与 Android 不同，**iOS App Store 政策硬禁 JIT**（仅 WKWebView 持有 dynamic-codesigning entitlement），因此本 spec 必须 interp-only + AOT 双轨：

- **interp-only** 是 v0.1 默认路径，先验证 z42 能在 iOS 上跑通
- **AOT 占位** 在本 spec 范围内只搭骨架（`features = ["aot"]` 编译开关），AOT 实际实现逻辑留给独立 spec

P4.4 是 P4 跨平台**最后一块**：

- 复用 P4.2 (wasm) 验证的 interp 平台无关性
- 复用 P4.3 (android) 的 jni-style 桥接经验（iOS 用 C ABI + Swift FFI，结构类似）

## What Changes

- **新建 [platform/ios/](platform/ios/) 目录**：
  - SwiftPM Package（Z42Runtime）
  - C bridge module（Z42RuntimeC）—— 暴露 Rust .a 给 Swift
  - Rust ios crate（cdylib + staticlib）—— iOS 与 simulator 各 ABI
  - iOSDemo Xcode 项目（SwiftUI 简单 App）
  - XCTest（simulator 上运行）
- **构建脚本** `platform/ios/build.sh`：用 `cargo` 多 target 构建 .a，然后用 `xcodebuild -create-xcframework` 打 xcframework
- **just 接入**：`just platform ios build` / `just platform ios test`
- **CI 接入**：macOS runner 上加 platform-ios job（要求 macOS 14+ 自带 Xcode）
- **文档**：[platform/ios/README.md](platform/ios/README.md) + [docs/design/cross-platform.md](docs/design/cross-platform.md) iOS 段

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `platform/ios/Package.swift` | NEW | SwiftPM manifest |
| `platform/ios/Sources/Z42Runtime/Z42Vm.swift` | NEW | Swift facade |
| `platform/ios/Sources/Z42Runtime/Z42Error.swift` | NEW | Swift error 类型 |
| `platform/ios/Sources/Z42RuntimeC/include/z42_ios.h` | NEW | C ABI 头文件（Swift 通过 modulemap 引用） |
| `platform/ios/Sources/Z42RuntimeC/include/module.modulemap` | NEW | clang module map |
| `platform/ios/Sources/Z42RuntimeC/z42_ios.c` | NEW | C glue（thin wrapper around Rust） |
| `platform/ios/Sources/Z42RuntimeC/z42_runtime.xcframework/` | NEW (binary) | 预编译 xcframework（不入仓，由 build.sh 产生） |
| `platform/ios/rust/Cargo.toml` | NEW | iOS Rust crate manifest |
| `platform/ios/rust/src/lib.rs` | NEW | C ABI 导出（不带 Java JNI） |
| `platform/ios/Tests/Z42RuntimeTests/Z42VmTests.swift` | NEW | XCTest |
| `platform/ios/Tests/Z42RuntimeTests/Resources/01_hello.zbc` | NEW | 测试资源 |
| `platform/ios/iOSDemo/iOSDemo.xcodeproj/project.pbxproj` | NEW | Xcode 项目 |
| `platform/ios/iOSDemo/iOSDemo/iOSDemoApp.swift` | NEW | SwiftUI App 入口 |
| `platform/ios/iOSDemo/iOSDemo/ContentView.swift` | NEW | UI |
| `platform/ios/iOSDemo/iOSDemo/Resources/01_hello.zbc` | NEW | demo 资源 |
| `platform/ios/build.sh` | NEW | cargo build × 3 target + xcodebuild create-xcframework |
| `platform/ios/.gitignore` | NEW | 忽略 .build/ DerivedData/ xcframework binary |
| `platform/ios/README.md` | NEW | 工程文档 |
| `justfile` | MODIFY | 加 `platform-ios-*` 子任务（替换 P4.3 留下的 ios 占位） |
| `.github/workflows/ci.yml` | MODIFY | 加 platform-ios job（macOS runner） |
| `docs/design/cross-platform.md` | MODIFY | iOS 段（架构 / Swift API / JIT 禁令） |
| `docs/dev.md` | MODIFY | 加 "Platform: iOS" 段 |
| `src/runtime/Cargo.toml` | MODIFY | `[workspace] members` 加 `../platform/ios/rust` |

**只读引用**：
- [platform/wasm/](platform/wasm/) — 借鉴 JS API 设计风格
- [platform/android/](platform/android/) — 借鉴 facade + native bridge 切分
- [src/runtime/](src/runtime/) — 理解 Interpreter API
- [docs/design/cross-platform.md](docs/design/cross-platform.md) — P4.1 / P4.2 / P4.3 已建好

## Out of Scope

- **JIT 启用**：App Store 政策禁；本 spec 不实现
- **AOT 实际实现**：本 spec 只做 feature 开关与编译验证；AOT 后端代码生成留独立 spec
- **App Store 上架准备**（签名、entitlements、ITC）：超出范围
- **macOS / tvOS / watchOS target**：本 spec 仅 iOS（包括 simulator）
- **Catalyst (iPad on Mac)**：超出范围
- **SwiftPM 发布到 Swift Package Index**：超出范围
- **Android 工程**（P4.3 范围）

## Open Questions

- [ ] **Q1**：用 SwiftPM 还是 CocoaPods？
  - 倾向：**SwiftPM**（Apple 官方主推；与 Xcode 集成最佳）
- [ ] **Q2**：xcframework 是否入 git？
  - 倾向：**不入**（二进制文件大；CI/build.sh 重新生成）
- [ ] **Q3**：min iOS 版本？
  - 倾向：**14.0**（覆盖 ≥ 95% 设备；SwiftUI 完整支持）
- [ ] **Q4**：simulator 用 arm64 还是 x86_64？
  - 倾向：**两者**（M 系列 mac 用 arm64-sim；Intel mac 与 GitHub Actions runner 用 x86_64-sim）
- [ ] **Q5**：CI 在 GitHub macos-14 / macos-15 runner 跑（默认 ARM）？
  - 倾向：macos-14 (M1)；x86_64-ios-sim 仍编译但 CI 测试只在 arm64-sim 跑
- [ ] **Q6**：C bridge 用 cbindgen 自动生成 .h 还是手写？
  - 倾向：手写（API 表面小；cbindgen 引入额外依赖）
