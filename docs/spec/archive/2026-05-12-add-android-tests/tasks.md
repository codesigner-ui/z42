# Tasks: 给 Android facade 加 JUnit instrumented test

> 状态：🟢 已完成 | 创建：2026-05-12 | 归档：2026-05-12 | 类型：test + fix（contract 实现 + 修 build.sh / cpp / CMake grandfather bugs）

## 进度概览

- [x] 阶段 1: 本地 Android 工具链 + Gradle wrapper
- [x] 阶段 2: build.sh grandfather fixes（cargo ndk cwd + cpp 转发头 `../` 计数）
- [x] 阶段 3: build.sh 编 test fixtures
- [x] 阶段 4: Gradle androidTest + Kotlin 测试代码
- [x] 阶段 5: test.sh emulator 自管 + 跑测试
- [x] 阶段 6: README + 文档同步 + 归档

## 阶段 1: 本地 Android 工具链 + Gradle wrapper

- [x] 1.1 `scripts/install-android-toolchain-local.sh` 写好 + 装通：SDK + NDK r26 + emulator + system-image arm64 + AVD `z42_pixel6_api34` + Gradle 8.7
- [x] 1.2 生成 `gradlew` / `gradle/wrapper/{gradle-wrapper.jar,gradle-wrapper.properties}` —— 由本地 Gradle 8.7 `gradle wrapper --gradle-version 8.7` 一次性产
- [x] 1.3 AAR build 验证：`./build.sh` 端到端成功，37 Gradle task 全过，产 z42vm-release.aar

## 阶段 2: build.sh grandfather fixes

- [x] 2.1 `build.sh` cargo ndk cwd —— `cd "$HERE/rust"` 后再调（cargo-ndk 不识别 `--manifest-path`）
- [x] 2.2 `z42vm/src/main/cpp/include/z42_host.h` 转发头 `../` 8 → 9（off-by-one）
- [x] 2.3 `z42vm/src/main/cpp/include/z42_abi.h` 同上

## 阶段 3: build.sh 编 test fixtures

- [x] 3.1 在 build.sh `# (4) Gradle assemble` 之前插入 `# (3.5) Compile test fixtures` 段
- [x] 3.2 编 `examples/embedding/{hello,multi_line}.z42 → src/androidTest/assets/test-fixtures/*.zbc`
- [x] 3.3 跑 `./build.sh` 确认 fixtures 在新路径

## 阶段 4: Gradle androidTest + Kotlin 测试代码

- [x] 4.1 `z42vm/build.gradle.kts` 加 `testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"`
- [x] 4.2 `z42vm/build.gradle.kts` 加 `androidTestImplementation` deps（androidx.test.ext:junit 1.2.1, runner 1.6.1, core 1.6.1）
- [x] 4.3 新建 `z42vm/src/androidTest/java/io/z42/vm/Z42VMInstrumentedTest.kt`
- [x] 4.4 实现 testSmokeHelloWorld (R1)
- [x] 4.5 实现 testBadZbcThrowsBadZbc (R2)
- [x] 4.6 实现 testResolveUnknownEntryThrowsEntryNotFound (R3)
- [x] 4.7 实现 testInvokeWrongArgCountThrowsArgMismatch (R4)
- [x] 4.8 实现 testMapResolverWithoutCorelibSurfacesAtInvoke (R5)
- [x] 4.9 实现 testInitShutdownLifecycleRoundtrip (R6)
- [x] 4.10 实现 testMultiLineStdoutPreservesOrder (R7)

## 阶段 5: test.sh emulator 自管 + 跑测试

- [x] 5.1 新建 `platforms/android/test.sh`
- [x] 5.2 启 emulator headless 后台 + adb wait-for-device + sys.boot_completed loop
- [x] 5.3 跑 `./gradlew :z42vm:connectedAndroidTest`
- [x] 5.4 `trap` 退出时 adb emu kill
- [x] 5.5 跑 `./test.sh`，确认 7/7 全过

## 阶段 6: README + 文档同步 + 归档

- [x] 6.1 `platforms/android/.gitignore` 加 `androidTest/assets/test-fixtures/*.zbc` + `.gradle/` (如未有)
- [x] 6.2 `README.md` 加 "Run tests" 段；Limitations 段把 "JUnit 推迟" 移除
- [x] 6.3 `docs/design/runtime/embedding.md` §11.7 Android 行更新（已支持 index）
- [x] 6.4 `./scripts/test-all.sh` 6 stage 不退步
- [x] 6.5 移 `changes/add-android-tests/` → `archive/2026-05-12-add-android-tests/`
- [x] 6.6 commit + push（type=test, scope=host/android）

## 实施备注（实际遇到的问题）

- **JNI dlopen 失败 — 第三个 grandfather bug**：实施时实测发现 `libz42vm_jni.so` 在 device 上 dlopen 失败，错误信息含 build-time 绝对路径 `/Users/.../jniLibs/arm64-v8a/libz42_platform_android.so`。根因：CMake 的 `IMPORTED` target 默认把 `IMPORTED_LOCATION`（绝对路径）记进 DT_NEEDED。修法：在 `z42vm/src/main/cpp/CMakeLists.txt` 给 `z42_platform_android` target 加 `IMPORTED_NO_SONAME TRUE`，让 CMake 只记 basename。这第三个 grandfather bug 与 cargo-ndk cwd + cpp 转发头 `../` 计数 串成一组"Android 端从来没在 device 上跑过所以没暴露"的连环 bug；本 spec 一并修。
- AGP 自动下载了 NDK 26.1（默认），与 installer 装的 26.3（symlink target）并存；空间 ~1.2GB 重复。功能正常。后续可在 build.gradle.kts 加 `ndkVersion = "26.3.11579264"` 显式 pin。
- emulator 第一次 boot ~30-60s，后续 reuse 时 ~0s。test.sh 复用已运行的 emulator（与 wasm playwright `reuseExistingServer` 同模式）。

## 备注

- emulator headless + swiftshader CPU 渲染（D2）—— Apple silicon 上不依赖 Metal，~30s boot
- AVD 由 installer 预创建（D3），test.sh 不重建
- 不进 test-all.sh 默认 GREEN（D5）—— ~4GB 工具链 + ~80s emulator boot 太重
- Android resolver namespace index 修复已经在 `fix-bundle-resolver-namespace-index` (3da1d4b8) 落地；本 spec 不动 Kotlin resolver
- Gradle wrapper 入仓；下游用户 clone 后 `./gradlew` 自动下载 Gradle 8.7 到 `GRADLE_USER_HOME`
