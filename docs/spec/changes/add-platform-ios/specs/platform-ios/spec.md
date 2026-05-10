# Spec: iOS Platform Scaffold

## ADDED Requirements

### Requirement: 工程目录结构

#### Scenario: platform/ios/ 含必要文件

- **WHEN** 检查 `platform/ios/`
- **THEN** 含 `Package.swift`、`Sources/`、`Tests/`、`iOSDemo/`、`rust/`、`build.sh`、`README.md`

#### Scenario: SwiftPM Package 解析正确

- **WHEN** 在 Xcode 打开 [platform/ios/Package.swift](platform/ios/Package.swift)
- **THEN** Xcode 能解析依赖，无错误

---

### Requirement: C ABI 头文件

#### Scenario: z42_ios.h 含 8 个核心函数

- **WHEN** 阅读 [platform/ios/Sources/Z42RuntimeC/include/z42_ios.h](platform/ios/Sources/Z42RuntimeC/include/z42_ios.h)
- **THEN** 含 `z42_vm_new`、`z42_vm_free`、`z42_vm_load_zbc`、`z42_vm_load_zpkg`、`z42_vm_run`、`z42_vm_set_stdout_handler`、`z42_vm_set_stderr_handler`、`z42_string_free`

#### Scenario: clang module map 暴露给 Swift

- **WHEN** 阅读 [platform/ios/Sources/Z42RuntimeC/include/module.modulemap](platform/ios/Sources/Z42RuntimeC/include/module.modulemap)
- **THEN** 含 `module Z42RuntimeC { header "z42_ios.h" export * }`

---

### Requirement: Rust crate

#### Scenario: cdylib + staticlib 双 crate-type

- **WHEN** 阅读 [platform/ios/rust/Cargo.toml](platform/ios/rust/Cargo.toml)
- **THEN** `[lib] crate-type = ["cdylib", "staticlib"]`

#### Scenario: 启用 z42-runtime ios feature

- **WHEN** 阅读依赖段
- **THEN** `z42-runtime` 用 `default-features = false, features = ["ios"]`

#### Scenario: 三 target 编译通过

- **WHEN** 执行 `cargo build --target <T>` for `aarch64-apple-ios` / `aarch64-apple-ios-sim` / `x86_64-apple-ios`
- **THEN** 三 target 各自编译通过

---

### Requirement: Swift facade

#### Scenario: Z42Vm 类完整

- **WHEN** 阅读 [platform/ios/Sources/Z42Runtime/Z42Vm.swift](platform/ios/Sources/Z42Runtime/Z42Vm.swift)
- **THEN** 含方法：`loadZpkg`、`loadZbc`、`setStdoutHandler`、`setStderrHandler`、`run`
- **AND** init 抛 throws；deinit 自动释放

#### Scenario: Z42Error 枚举

- **WHEN** 阅读 [platform/ios/Sources/Z42Runtime/Z42Error.swift](platform/ios/Sources/Z42Runtime/Z42Error.swift)
- **THEN** 含 `initializationFailed` / `loadFailed(String)` / `runFailed(String)` 三个 case

---

### Requirement: xcframework 构建

#### Scenario: build.sh 产出 xcframework

- **WHEN** 执行 `./platform/ios/build.sh release`
- **THEN** 在 `Sources/Z42RuntimeC/z42_runtime.xcframework` 产出 xcframework
- **AND** xcframework 含 `ios-arm64/` 和 `ios-arm64_x86_64-simulator/` 两个 slice

#### Scenario: simulator slice 含 fat binary

- **WHEN** 检查 `ios-arm64_x86_64-simulator/libz42_ios.a`
- **THEN** `lipo -info` 输出含 `arm64 x86_64`

---

### Requirement: SwiftPM 测试

#### Scenario: simulator 上 Z42VmTests 通过

- **WHEN** 执行 `xcodebuild test -scheme Z42Runtime -destination 'platform=iOS Simulator,name=iPhone 15,OS=latest'`
- **THEN** Z42VmTests.testHelloWorldRuns 通过
- **AND** 断言 captured stdout == "Hello, World!\n"

---

### Requirement: iOSDemo App

#### Scenario: 在 simulator 上启动显示 Hello

- **WHEN** Xcode 在 iOS Simulator 启动 iOSDemo
- **THEN** ContentView 显示 "Hello, World!"
- **AND** 无 crash

#### Scenario: 编译为 Release

- **WHEN** 执行 `xcodebuild -scheme iOSDemo -configuration Release build`
- **THEN** 编译成功

---

### Requirement: 跨平台一致性

#### Scenario: vm_core 子集在 iOS 跑通

- **WHEN** 选取 5 个 vm_core .zbc 嵌入 iOSDemo Resources
- **WHEN** 在 simulator 上依次 loadZbc + run
- **THEN** 每个 .zbc 输出与 desktop interp 完全一致

---

### Requirement: just 入口

#### Scenario: just platform ios build

- **WHEN** 执行 `just platform ios build`
- **THEN** 触发 `./platform/ios/build.sh release`，产出 xcframework

#### Scenario: just platform ios test

- **WHEN** 执行 `just platform ios test`
- **THEN** 触发 simulator 上的 XCTest

---

### Requirement: CI 接入

#### Scenario: platform-ios job

- **WHEN** PR 触发 CI
- **THEN** 含 `platform-ios` job（macos-14 runner）
- **AND** 安装 3 个 iOS rust target
- **AND** 跑 build + test，全绿

---

### Requirement: 政策/限制声明

#### Scenario: 文档明确 JIT 禁令

- **WHEN** 阅读 [docs/design/cross-platform.md](docs/design/cross-platform.md) iOS 章节
- **THEN** 明确说明 "iOS App Store 政策禁止 JIT；本平台仅 interp + AOT"
- **AND** 引用 Apple 官方政策文档链接

#### Scenario: features ios 不含 jit

- **WHEN** 检查 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) `ios` feature
- **THEN** 等于 `["interp-only", "aot"]`，**不含** `jit`

---

### Requirement: 文档同步

#### Scenario: cross-platform.md 含 iOS 章节

- **WHEN** 阅读 [docs/design/cross-platform.md](docs/design/cross-platform.md)
- **THEN** 含 "iOS" 章节：架构图、Swift API、xcframework 集成、JIT 禁令

#### Scenario: platform/ios/README.md 完整

- **WHEN** 阅读 [platform/ios/README.md](platform/ios/README.md)
- **THEN** 含工具安装（Xcode 15+）、build 步骤、SwiftPM 集成示例、demo 运行

#### Scenario: dev.md 含 iOS 段

- **WHEN** 阅读 [docs/dev.md](docs/dev.md)
- **THEN** 含 "Platform: iOS" 段，列出 just 命令
