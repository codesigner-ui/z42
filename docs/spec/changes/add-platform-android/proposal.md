# Proposal: Add Android Platform Scaffold

## Why

z42 当前**无 Android 支持**。Android 是移动端最大平台，让 z42 能在 Android App 内嵌入是关键路线之一。

P4.3 在 P4.2 (wasm) 验证 interp 路径平台无关性后启动；与 wasm 不同，Android 上 JIT 技术可行（Cranelift aarch64 + W^X 支持），但本 spec 默认 **interp-only 起步**，JIT 启用留给后置的"Android JIT 兼容性实测"独立 spec。

## What Changes

- **新建 [platform/android/](platform/android/) 目录**：
  - Gradle 多 module 项目
  - z42-runtime AAR module（Java/Kotlin facade + JNI 入口 + Rust .so 通过 cargo-ndk 构建）
  - demo-app module（Android App，加载 .zbc 跑通）
  - androidTest（emulator 上的 JUnit）
- **构建脚本** `platform/android/build.sh`：用 `cargo-ndk` 构建 4 个 ABI 的 .so，然后 Gradle 打 AAR
- **just 接入**：`just platform android build` / `just platform android test`
- **CI 接入**：linux runner 上加 platform-android job（用 macOS 也可，因 macOS runner 提供 Android emulator x86_64 加速）
- **文档**：[platform/android/README.md](platform/android/README.md) + [docs/design/cross-platform.md](docs/design/cross-platform.md) Android 段

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `platform/android/build.gradle.kts` | NEW | Root project Gradle |
| `platform/android/settings.gradle.kts` | NEW | Module 注册 |
| `platform/android/gradle.properties` | NEW | Gradle 全局属性 |
| `platform/android/gradlew` / `gradlew.bat` | NEW | Gradle wrapper |
| `platform/android/gradle/wrapper/gradle-wrapper.properties` | NEW | wrapper 版本 |
| `platform/android/.gitignore` | NEW | 忽略 build/ .gradle/ local.properties |
| `platform/android/README.md` | NEW | 工程文档 |
| `platform/android/z42-runtime/build.gradle.kts` | NEW | AAR module Gradle |
| `platform/android/z42-runtime/AndroidManifest.xml` | NEW | minimal manifest |
| `platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Vm.kt` | NEW | Kotlin facade |
| `platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Exception.kt` | NEW | 异常类 |
| `platform/android/z42-runtime/src/main/cpp/CMakeLists.txt` | NEW | 仅链接 .so 用，不编译 C++ |
| `platform/android/z42-runtime/src/main/rust/Cargo.toml` | NEW | JNI bridge crate（cdylib，依赖 z42-runtime） |
| `platform/android/z42-runtime/src/main/rust/src/lib.rs` | NEW | jni crate 入口 |
| `platform/android/z42-runtime/src/androidTest/java/.../Z42VmTest.kt` | NEW | JUnit instrumented test |
| `platform/android/demo-app/build.gradle.kts` | NEW | demo App Gradle |
| `platform/android/demo-app/AndroidManifest.xml` | NEW | App manifest |
| `platform/android/demo-app/src/main/java/.../MainActivity.kt` | NEW | 简单 UI 显示 vm 输出 |
| `platform/android/demo-app/src/main/res/layout/activity_main.xml` | NEW | UI layout |
| `platform/android/demo-app/src/main/assets/01_hello.zbc` | NEW | 内嵌示例 |
| `platform/android/build.sh` | NEW | cargo-ndk + gradle assembleRelease |
| `justfile` | MODIFY | 加 `platform-android-*` 子任务（替换 P4.2 留下的 android 占位） |
| `.github/workflows/ci.yml` | MODIFY | 加 platform-android job（macOS runner 用 emulator） |
| `docs/design/cross-platform.md` | MODIFY | Android 段（架构 / API / 限制） |
| `docs/dev.md` | MODIFY | 加 "Platform: Android" 段 |
| `src/runtime/Cargo.toml` | MODIFY | `[workspace] members` 加 `../platform/android/z42-runtime/src/main/rust` |

**只读引用**：
- [platform/wasm/](platform/wasm/) — 借鉴 P4.2 的 JS API 设计风格
- [src/runtime/](src/runtime/) — 理解 Interpreter API
- [docs/design/cross-platform.md](docs/design/cross-platform.md) — P4.1 / P4.2 已建好

## Out of Scope

- **JIT 启用**：本 spec 默认 interp-only；JIT 留给后置实测 spec
- **Google Play 上架准备**（签名、proguard、ABI splits 优化）：超出范围
- **Compose UI 集成**：本 spec demo 用传统 XML layout，最简
- **Kotlin Multiplatform**（KMP）：超出范围
- **Android Studio 模板向导**（New Project Wizard 集成）：超出范围
- **Android 的 z42 stdlib 平台特定 API**（如 SharedPreferences 桥接）：超出范围
- **iOS 工程**（P4.4 范围）

## Open Questions

- [ ] **Q1**：JNI bridge 用 jni 0.21 还是 robusta？
  - 倾向：jni 0.21（事实标准、最少抽象）
- [ ] **Q2**：AAR 是否同时打包 .so + Java 类？
  - 倾向：是（标准做法）
- [ ] **Q3**：CI 跑 emulator 还是 unit test only？
  - 倾向：unit test（JVM）+ 用 macOS runner 跑 connectedAndroidTest（emulator）
- [ ] **Q4**：min SDK 版本？
  - 倾向：API 24（Android 7.0），覆盖 99% 设备且支持 W^X
- [ ] **Q5**：默认编译哪些 ABI？
  - 倾向：arm64-v8a + armeabi-v7a + x86_64（emulator 用）
- [ ] **Q6**：cargo-ndk 还是 cross？
  - 倾向：cargo-ndk（专用，更稳）
