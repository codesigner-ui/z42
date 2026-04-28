# Tasks: Add Android Platform Scaffold

> 状态：🔵 DRAFT（未实施） | 创建：2026-04-29
> 依赖 P4.1 + P4.2 完成。本文件锁定接口契约。

## 进度概览

- [ ] 阶段 1: Gradle 工程骨架
- [ ] 阶段 2: Rust JNI bridge crate
- [ ] 阶段 3: Kotlin facade
- [ ] 阶段 4: 构建脚本
- [ ] 阶段 5: demo App
- [ ] 阶段 6: instrumented test
- [ ] 阶段 7: just / CI 接入
- [ ] 阶段 8: 文档同步
- [ ] 阶段 9: 验证

---

## 阶段 1: Gradle 工程骨架

- [ ] 1.1 [platform/android/settings.gradle.kts](platform/android/settings.gradle.kts) module 注册
- [ ] 1.2 [platform/android/build.gradle.kts](platform/android/build.gradle.kts) root project
- [ ] 1.3 [platform/android/gradle.properties](platform/android/gradle.properties) JVM 内存等
- [ ] 1.4 Gradle wrapper（gradlew + gradle-wrapper.properties + .jar）
- [ ] 1.5 [platform/android/.gitignore](platform/android/.gitignore) build/ .gradle/ local.properties
- [ ] 1.6 [platform/android/README.md](platform/android/README.md)
- [ ] 1.7 [platform/android/z42-runtime/build.gradle.kts](platform/android/z42-runtime/build.gradle.kts) AAR module
- [ ] 1.8 [platform/android/z42-runtime/AndroidManifest.xml](platform/android/z42-runtime/AndroidManifest.xml) minimal

## 阶段 2: Rust JNI bridge crate

- [ ] 2.1 [platform/android/z42-runtime/src/main/rust/Cargo.toml](platform/android/z42-runtime/src/main/rust/Cargo.toml) crate manifest
- [ ] 2.2 [platform/android/z42-runtime/src/main/rust/src/lib.rs](platform/android/z42-runtime/src/main/rust/src/lib.rs) JNI 入口（5 个函数）
- [ ] 2.3 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) workspace members 加 `../platform/android/z42-runtime/src/main/rust`
- [ ] 2.4 验证：`cargo build --target aarch64-linux-android -p z42-android` 通过（需 cargo-ndk）

## 阶段 3: Kotlin facade

- [ ] 3.1 [platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Vm.kt](platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Vm.kt) facade
- [ ] 3.2 [platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Exception.kt](platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Exception.kt)
- [ ] 3.3 验证：`gradlew :z42-runtime:compileDebugKotlin` 通过

## 阶段 4: 构建脚本

- [ ] 4.1 [platform/android/build.sh](platform/android/build.sh) cargo-ndk 4 ABI + gradle assemble
- [ ] 4.2 chmod +x
- [ ] 4.3 build.sh 内含可选预编译 examples/01_hello.z42 步骤

## 阶段 5: demo App

- [ ] 5.1 [platform/android/demo-app/build.gradle.kts](platform/android/demo-app/build.gradle.kts)
- [ ] 5.2 [platform/android/demo-app/AndroidManifest.xml](platform/android/demo-app/AndroidManifest.xml)
- [ ] 5.3 [platform/android/demo-app/src/main/java/com/codesigner/z42/demo/MainActivity.kt](platform/android/demo-app/src/main/java/com/codesigner/z42/demo/MainActivity.kt)
- [ ] 5.4 [platform/android/demo-app/src/main/res/layout/activity_main.xml](platform/android/demo-app/src/main/res/layout/activity_main.xml)
- [ ] 5.5 编译 examples/01_hello.z42 → assets/01_hello.zbc
- [ ] 5.6 验证：`gradlew :demo-app:assembleDebug` 通过

## 阶段 6: instrumented test

- [ ] 6.1 [platform/android/z42-runtime/src/androidTest/java/com/codesigner/z42/Z42VmTest.kt](platform/android/z42-runtime/src/androidTest/java/com/codesigner/z42/Z42VmTest.kt)
- [ ] 6.2 把 examples/01_hello.zbc 复制到 androidTest assets
- [ ] 6.3 加 5 个 vm_core .zbc 跨平台一致性测试

## 阶段 7: just / CI 接入

- [ ] 7.1 [justfile](justfile) `platform` 子命令扩展（android case）
- [ ] 7.2 加 `platform-android-build` / `platform-android-test` / `platform-android-test-emulator` / `platform-android-demo-install`
- [ ] 7.3 [.github/workflows/ci.yml](.github/workflows/ci.yml) 加 `platform-android` job（macOS runner）
- [ ] 7.4 CI 用 `reactivecircus/android-emulator-runner@v2` 跑 emulator 测试

## 阶段 8: 文档同步

- [ ] 8.1 [docs/design/cross-platform.md](docs/design/cross-platform.md) 加 "Android" 章节
- [ ] 8.2 [platform/android/README.md](platform/android/README.md) 完整文档（工具、build、AAR 集成示例）
- [ ] 8.3 [docs/dev.md](docs/dev.md) 加 "Platform: Android" 段
- [ ] 8.4 [docs/roadmap.md](docs/roadmap.md) 进度表加 P4.3 完成

## 阶段 9: 验证

- [ ] 9.1 `./platform/android/build.sh release` 产出 AAR 在 `z42-runtime/build/outputs/aar/`
- [ ] 9.2 AAR 解压含 4 个 ABI 的 libz42android.so
- [ ] 9.3 各 .so 大小 < 5 MB（release）
- [ ] 9.4 AAR 总大小 ≤ 8 MB
- [ ] 9.5 `gradlew :z42-runtime:testDebugUnitTest` JVM 单测通过
- [ ] 9.6 `gradlew :z42-runtime:connectedDebugAndroidTest` emulator instrumented 通过
- [ ] 9.7 demo-app 安装到 emulator 启动显示 "Hello, World!"
- [ ] 9.8 5 个 vm_core 用例在 Android 跑出与 desktop 一致输出
- [ ] 9.9 CI platform-android job 全绿

## 备注

### 实施依赖

- 必须先完成 P4.1（feature flags）
- 强烈建议在 P4.2 (wasm) 之后做（复用 cross-platform.md 结构）

### 风险

- **风险 1**：cargo-ndk 与新 NDK 版本兼容性问题 → 锁定 NDK r26 + cargo-ndk 3.5
- **风险 2**：emulator CI 启动慢（5–10 分钟） → 用 reactivecircus/android-emulator-runner 缓存 AVD
- **风险 3**：JNI 字符串编码（UTF-16 vs UTF-8） → jni 0.21 提供 `get_string`，已封装好
- **风险 4**：Z42Vm.onStdout 回调跨线程问题 → demo 用 `runOnUiThread`；androidTest 同步获取
- **风险 5**：cargo workspace 跨极深路径可能不被 cargo 接受 → fallback 是脱离 workspace
- **风险 6**：z42-runtime crate 可能引用 std::process 等 wasm/Android 不可用 API → 实施前 `cargo check --target aarch64-linux-android` 列出错误

### 工作量估计

3–4 天（Gradle 配置 + JNI bridge 是大头；emulator CI 调试可能额外占半天）。
