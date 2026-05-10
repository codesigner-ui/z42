# Spec: Android Platform Scaffold

## ADDED Requirements

### Requirement: 工程目录结构

#### Scenario: platform/android/ 含必要文件

- **WHEN** 检查 `platform/android/`
- **THEN** 含 `build.gradle.kts`、`settings.gradle.kts`、`gradlew`、`build.sh`、`README.md`
- **AND** 含 `z42-runtime/`（AAR module）和 `demo-app/`（demo App module）

#### Scenario: Gradle wrapper 已就绪

- **WHEN** 检查 `platform/android/gradlew`
- **THEN** 文件存在且可执行
- **AND** `gradle/wrapper/gradle-wrapper.properties` 含 Gradle 8.5+ 版本

---

### Requirement: AAR module 结构

#### Scenario: namespace 与 minSdk 正确

- **WHEN** 阅读 [platform/android/z42-runtime/build.gradle.kts](platform/android/z42-runtime/build.gradle.kts)
- **THEN** `namespace = "com.codesigner.z42"`，`minSdk = 24`，`compileSdk >= 34`

#### Scenario: 三 ABI 支持

- **WHEN** 阅读 `defaultConfig.ndk.abiFilters`
- **THEN** 含 `arm64-v8a`、`armeabi-v7a`、`x86_64`

---

### Requirement: Kotlin facade

#### Scenario: Z42Vm 类完整

- **WHEN** 阅读 [platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Vm.kt](platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Vm.kt)
- **THEN** 含方法：`loadZpkg`、`loadZbc`、`setStdoutHandler`、`setStderrHandler`、`run`、`close`
- **AND** 实现 `Closeable`

#### Scenario: native 库加载

- **WHEN** Z42Vm 类首次访问
- **THEN** companion object init 块调用 `System.loadLibrary("z42android")`

#### Scenario: 异常封装

- **WHEN** native 端报错
- **THEN** Java 端抛出 `Z42Exception`（含 message 与可选 errorCode）

---

### Requirement: Rust JNI bridge

#### Scenario: cdylib 输出 libz42android.so

- **WHEN** 阅读 [platform/android/z42-runtime/src/main/rust/Cargo.toml](platform/android/z42-runtime/src/main/rust/Cargo.toml)
- **THEN** `[lib] crate-type = ["cdylib"]` 且 `name = "z42android"`

#### Scenario: 启用 z42-runtime android feature

- **WHEN** 阅读依赖段
- **THEN** `z42-runtime` 用 `default-features = false, features = ["android"]`

#### Scenario: JNI 函数签名匹配

- **WHEN** 阅读 [src/lib.rs](platform/android/z42-runtime/src/main/rust/src/lib.rs)
- **THEN** 含 `Java_com_codesigner_z42_Z42Vm_nativeNew/Free/LoadZbc/LoadZpkg/Run` 5 个 `#[no_mangle] extern "system"` 函数
- **AND** 函数签名与 Kotlin 端 `external fun` 一致

---

### Requirement: 构建工具

#### Scenario: build.sh 产出 AAR

- **WHEN** 执行 `./platform/android/build.sh release`
- **THEN** 在 `z42-runtime/build/outputs/aar/` 产出 `z42-runtime-release.aar`
- **AND** AAR 解压后含 4 个 ABI（arm64-v8a / armeabi-v7a / x86_64）的 `lib/<abi>/libz42android.so`

#### Scenario: cargo-ndk 4 ABI 编译

- **WHEN** build.sh 执行
- **THEN** cargo-ndk 依次编译 arm64-v8a、armeabi-v7a、x86_64 三个 ABI 的 .so
- **AND** 各 .so 文件大小 < 5 MB（release）

---

### Requirement: demo App

#### Scenario: demo-app 可安装到 emulator

- **WHEN** 执行 `gradlew :demo-app:installDebug`
- **THEN** APK 安装成功

#### Scenario: 启动显示 Hello

- **WHEN** demo App 在 emulator 启动
- **THEN** UI 显示 "Hello, World!"
- **AND** 无 crash / ANR

---

### Requirement: instrumented test

#### Scenario: emulator 上 helloWorldRuns 通过

- **WHEN** 执行 `gradlew :z42-runtime:connectedDebugAndroidTest`
- **THEN** Z42VmTest.helloWorldRuns 通过
- **AND** 断言 captured stdout == "Hello, World!\n"

#### Scenario: JVM 单测通过

- **WHEN** 执行 `gradlew :z42-runtime:testDebugUnitTest`
- **THEN** 至少 1 个 JVM 单测通过（不依赖 native lib 的 facade 测试）

---

### Requirement: 跨平台一致性

#### Scenario: vm_core 子集在 Android 跑通

- **WHEN** 选取 5 个 vm_core .zbc 嵌入 demo-app assets
- **WHEN** 在 emulator 上依次 loadZbc + run
- **THEN** 每个 .zbc 输出与 desktop interp 完全一致

---

### Requirement: just 入口

#### Scenario: just platform android build

- **WHEN** 执行 `just platform android build`
- **THEN** 触发 `./platform/android/build.sh release`，产出 AAR

#### Scenario: just platform android test

- **WHEN** 执行 `just platform android test`
- **THEN** 触发 JVM 单测

#### Scenario: just platform android test-emulator

- **WHEN** 执行 `just platform android test-emulator`
- **THEN** 触发 connectedAndroidTest（要求 emulator 已启动）

---

### Requirement: CI 接入

#### Scenario: platform-android job

- **WHEN** PR 触发 CI
- **THEN** 含 `platform-android` job（macOS runner）
- **AND** 安装 cargo-ndk + Android SDK + JDK 17
- **AND** 跑 build + JVM 单测 + emulator 测试，全绿

---

### Requirement: 文档同步

#### Scenario: cross-platform.md 含 Android 章节

- **WHEN** 阅读 [docs/design/cross-platform.md](docs/design/cross-platform.md)
- **THEN** 含 "Android" 章节：架构图、Kotlin API、JNI bridge、AAR 集成、限制（默认 interp-only）

#### Scenario: platform/android/README.md 完整

- **WHEN** 阅读 [platform/android/README.md](platform/android/README.md)
- **THEN** 含工具安装（cargo-ndk / Android SDK / NDK）、build 步骤、AAR 集成示例、demo 运行

#### Scenario: dev.md 含 Android 段

- **WHEN** 阅读 [docs/dev.md](docs/dev.md)
- **THEN** 含 "Platform: Android" 段，列出 just 命令
