# Tasks: Add iOS Platform Scaffold

> 状态：🔵 DRAFT（未实施） | 创建：2026-04-29
> 依赖 P4.1 + P4.2 + P4.3 完成。本文件锁定接口契约。

## 进度概览

- [ ] 阶段 1: SwiftPM Package 骨架
- [ ] 阶段 2: C bridge 模块
- [ ] 阶段 3: Rust ios crate
- [ ] 阶段 4: Swift facade
- [ ] 阶段 5: 构建脚本（xcframework）
- [ ] 阶段 6: XCTest
- [ ] 阶段 7: iOSDemo Xcode 项目
- [ ] 阶段 8: just / CI 接入
- [ ] 阶段 9: 文档同步
- [ ] 阶段 10: 验证

---

## 阶段 1: SwiftPM Package 骨架

- [ ] 1.1 [platform/ios/Package.swift](platform/ios/Package.swift) Swift 5.9 manifest
- [ ] 1.2 [platform/ios/.gitignore](platform/ios/.gitignore) 忽略 .build/ DerivedData/ xcframework binary
- [ ] 1.3 [platform/ios/README.md](platform/ios/README.md) 工程文档

## 阶段 2: C bridge 模块

- [ ] 2.1 [platform/ios/Sources/Z42RuntimeC/include/z42_ios.h](platform/ios/Sources/Z42RuntimeC/include/z42_ios.h) 完整 C ABI 头
- [ ] 2.2 [platform/ios/Sources/Z42RuntimeC/include/module.modulemap](platform/ios/Sources/Z42RuntimeC/include/module.modulemap) clang module
- [ ] 2.3 [platform/ios/Sources/Z42RuntimeC/z42_ios.c](platform/ios/Sources/Z42RuntimeC/z42_ios.c) thin C glue（仅 include header）

## 阶段 3: Rust ios crate

- [ ] 3.1 [platform/ios/rust/Cargo.toml](platform/ios/rust/Cargo.toml) crate manifest
- [ ] 3.2 [platform/ios/rust/src/lib.rs](platform/ios/rust/src/lib.rs) C ABI 导出（8 个函数）
- [ ] 3.3 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) workspace members 加 `../platform/ios/rust`
- [ ] 3.4 验证：`cargo build --target aarch64-apple-ios -p z42-ios` 通过

## 阶段 4: Swift facade

- [ ] 4.1 [platform/ios/Sources/Z42Runtime/Z42Vm.swift](platform/ios/Sources/Z42Runtime/Z42Vm.swift) Swift facade
- [ ] 4.2 [platform/ios/Sources/Z42Runtime/Z42Error.swift](platform/ios/Sources/Z42Runtime/Z42Error.swift) 错误类型

## 阶段 5: 构建脚本

- [ ] 5.1 [platform/ios/build.sh](platform/ios/build.sh) cargo × 3 target + lipo + xcodebuild create-xcframework
- [ ] 5.2 chmod +x
- [ ] 5.3 build.sh 内含可选预编译 examples 步骤

## 阶段 6: XCTest

- [ ] 6.1 [platform/ios/Tests/Z42RuntimeTests/Z42VmTests.swift](platform/ios/Tests/Z42RuntimeTests/Z42VmTests.swift)
- [ ] 6.2 [platform/ios/Tests/Z42RuntimeTests/Resources/01_hello.zbc](platform/ios/Tests/Z42RuntimeTests/Resources/01_hello.zbc) 测试资源
- [ ] 6.3 加 5 个 vm_core 跨平台一致性测试

## 阶段 7: iOSDemo Xcode 项目

- [ ] 7.1 [platform/ios/iOSDemo/iOSDemo.xcodeproj](platform/ios/iOSDemo/iOSDemo.xcodeproj) Xcode 项目
- [ ] 7.2 [platform/ios/iOSDemo/iOSDemo/iOSDemoApp.swift](platform/ios/iOSDemo/iOSDemo/iOSDemoApp.swift) App 入口
- [ ] 7.3 [platform/ios/iOSDemo/iOSDemo/ContentView.swift](platform/ios/iOSDemo/iOSDemo/ContentView.swift) UI
- [ ] 7.4 SwiftPM 依赖配置（指向本地 ../Package.swift）
- [ ] 7.5 编译 examples/01_hello.z42 → iOSDemo/Resources/01_hello.zbc

## 阶段 8: just / CI 接入

- [ ] 8.1 [justfile](justfile) `platform` 子命令扩展（ios case）
- [ ] 8.2 加 `platform-ios-build` / `platform-ios-build-debug` / `platform-ios-test` / `platform-ios-demo-run`
- [ ] 8.3 [.github/workflows/ci.yml](.github/workflows/ci.yml) 加 `platform-ios` job（macos-14 runner）

## 阶段 9: 文档同步

- [ ] 9.1 [docs/design/cross-platform.md](docs/design/cross-platform.md) 加 "iOS" 章节（含 JIT 禁令声明）
- [ ] 9.2 [platform/ios/README.md](platform/ios/README.md) 完整文档（Xcode 安装、build、SwiftPM 集成）
- [ ] 9.3 [docs/dev.md](docs/dev.md) 加 "Platform: iOS" 段
- [ ] 9.4 [docs/roadmap.md](docs/roadmap.md) 进度表加 P4.4 完成（P4 全部完成）

## 阶段 10: 验证

- [ ] 10.1 `cargo build --target aarch64-apple-ios -p z42-ios` 通过
- [ ] 10.2 `cargo build --target aarch64-apple-ios-sim -p z42-ios` 通过
- [ ] 10.3 `cargo build --target x86_64-apple-ios -p z42-ios` 通过
- [ ] 10.4 `./platform/ios/build.sh release` 产出 xcframework
- [ ] 10.5 xcframework 含 `ios-arm64/` 和 `ios-arm64_x86_64-simulator/`
- [ ] 10.6 `lipo -info` 验证 simulator slice 含 arm64 + x86_64
- [ ] 10.7 `xcodebuild test -scheme Z42Runtime -destination 'platform=iOS Simulator,name=iPhone 15'` 通过
- [ ] 10.8 iOSDemo 在 simulator 启动显示 "Hello, World!"
- [ ] 10.9 5 个 vm_core 用例在 iOS 跑出与 desktop 一致输出
- [ ] 10.10 xcframework 总大小 ≤ 15 MB
- [ ] 10.11 CI platform-ios job 全绿

## 备注

### 实施依赖

- 必须先完成 P4.1（feature flags）
- 强烈建议在 P4.2、P4.3 之后做（复用 cross-platform.md 与 facade 经验）
- 本机必须有 Xcode 15+

### 风险

- **风险 1**：xcframework 创建时 architecture 冲突（多个 slice 含相同 arch） → lipo 合并 simulator slice 后再传给 xcodebuild
- **风险 2**：z42-runtime crate 引用 std::process::exit / fork → iOS 沙箱禁；feature ios 已是 interp-only + aot，不应触发
- **风险 3**：Swift / Rust ABI mismatch（C 层 callback 传 Swift closure） → v0.1 先 fallback 到 NSLog；完整桥接 v0.2
- **风险 4**：CI macos-14 simulator 启动慢 → 用 `xcrun simctl` 提前启动
- **风险 5**：Xcode 版本变更导致项目格式不兼容 → 锁定 Xcode 15.x；CI 用 `xcversion select 15`
- **风险 6**：iOSDemo .xcodeproj/project.pbxproj 是二进制级敏感文件，手写易错 → 用 Xcode 创建后入仓

### 工作量估计

3–4 天（Xcode 项目配置 + xcframework 构建是大头；CI simulator 调试可能额外占半天）。
