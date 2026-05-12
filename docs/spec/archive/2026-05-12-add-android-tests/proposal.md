# Proposal: 给 Android facade 加 instrumented test（add-android-tests）

## Why

Android facade 落地 (`2026-05-12-add-platform-android`) 把 Demo / JUnit / CI 推迟到独立 spec。本 spec 落地 JUnit instrumented test 部分，按照 [`platform-test-contract`](../../archive/2026-05-12-define-platform-test-contract/specs/platform-test-contract/spec.md) R1–R7 实现 Kotlin facade 的最小自动化测试集，与已有的 add-ios-tests / add-wasm-tests 平行对齐。

落地后效果：`./scripts/install-android-toolchain-local.sh && cd platforms/android && ./build.sh && ./test.sh` 在 Pixel 6 API 34 emulator 上跑通 7 个 contract scenario；终端用户 `import io.z42.vm.Z42VM` 装 `AssetZpkgResolver(context.assets)` 后直接能用 `Std.IO` namespace（已由 `fix-bundle-resolver-namespace-index` 修好）。

## What Changes

1. **`scripts/install-android-toolchain-local.sh`** —— 已在本 spec 准备期落地：装 SDK + NDK r26 + emulator + AVD + Gradle 8.7 到 `artifacts/tools/`，不动系统（已 commit-ready）
2. **`platforms/android/build.sh`** —— 修两处真实 bug：(a) `cargo ndk` 不识别 `--manifest-path`，要 `cd rust/` 后再调；(b) `cpp/include/z42_*.h` 转发头的 `../` 数错（8 应为 9），导致 NDK 编不过 JNI 桥
3. **新增 gradle wrapper** —— `gradlew` + `gradle/wrapper/` 由本地 Gradle 一次性 generate，commit 入仓
4. **新增 `platforms/android/z42vm/src/androidTest/`** —— Kotlin instrumented test 实现 R1–R7
5. **`platforms/android/z42vm/build.gradle.kts`** —— 加 androidTest 依赖（junit + androidx.test.runner + ext）
6. **新增 `platforms/android/test.sh`** —— boot emulator + `./gradlew :z42vm:connectedAndroidTest`
7. **build.sh** —— 加编 test fixtures (`examples/embedding/*.z42 → src/androidTest/assets/test-fixtures/*.zbc`)
8. **README + Limitations 段** —— 移除"测试 / JUnit / CI: 推迟"中的 tests，留下 demo / CI

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/install-android-toolchain-local.sh`                                                      | NEW    | SDK + NDK + emulator + AVD + Gradle 8.7 一站式安装到 `artifacts/tools/` |
| `src/toolchain/host/platforms/android/build.sh`                                                   | MODIFY | (a) cargo ndk cwd 修复；(b) 编 test fixtures 到 androidTest/assets/test-fixtures/ |
| `src/toolchain/host/platforms/android/z42vm/src/main/cpp/include/z42_host.h`                      | MODIFY | 转发头 `../` 从 8 改为 9 |
| `src/toolchain/host/platforms/android/z42vm/src/main/cpp/include/z42_abi.h`                       | MODIFY | 同上 |
| `src/toolchain/host/platforms/android/gradlew`                                                    | NEW    | Gradle wrapper（由本地 gradle 8.7 generate）|
| `src/toolchain/host/platforms/android/gradlew.bat`                                                | NEW    | Windows wrapper |
| `src/toolchain/host/platforms/android/gradle/wrapper/gradle-wrapper.jar`                          | NEW    | Wrapper bootstrap |
| `src/toolchain/host/platforms/android/gradle/wrapper/gradle-wrapper.properties`                   | NEW    | 锁 Gradle 版本 8.7 |
| `src/toolchain/host/platforms/android/z42vm/build.gradle.kts`                                     | MODIFY | androidTest deps（junit / androidx.test）; testInstrumentationRunner 配置 |
| `src/toolchain/host/platforms/android/z42vm/src/androidTest/java/io/z42/vm/Z42VMInstrumentedTest.kt` | NEW | R1–R7 JUnit 实现 |
| `src/toolchain/host/platforms/android/z42vm/src/androidTest/AndroidManifest.xml`                  | NEW    | 测试 manifest（部分 AGP 版本可省略；保险起见加） |
| `src/toolchain/host/platforms/android/test.sh`                                                    | NEW    | boot emulator → wait-for-boot → gradlew connectedAndroidTest |
| `src/toolchain/host/platforms/android/.gitignore`                                                 | MODIFY | 加 `androidTest/assets/test-fixtures/*.zbc`、`.gradle/`（如未有）|
| `src/toolchain/host/platforms/android/README.md`                                                  | MODIFY | 加 "Run tests" 段；Limitations 移除 JUnit 推迟 |
| `docs/design/runtime/embedding.md`                                                                | MODIFY | §11.7 Android 行：注明已支持 namespace index resolver |
| `docs/spec/changes/add-android-tests/{proposal,design,tasks}.md`                                  | NEW    | 本 spec 文档 |
| `docs/spec/changes/add-android-tests/specs/android-tests/spec.md`                                 | NEW    | R1–R7 in JUnit |

**只读引用：**

- `examples/embedding/{hello,multi_line}.z42` — 共享 fixture（iOS / wasm 也用）
- `src/toolchain/host/platforms/android/z42vm/src/main/java/io/z42/vm/*.kt` — Z42VM Kotlin API（不动）
- `docs/spec/archive/2026-05-12-define-platform-test-contract/specs/platform-test-contract/spec.md` — R1–R7 契约
- `docs/spec/archive/2026-05-12-add-ios-tests/` — XCTest 对照（结构平移到 Kotlin）

## Out of Scope

- iOS simulator 跑 XCTest（独立 `add-ios-ci` spec）
- Android Demo app（独立 `add-android-demo` spec）
- CI 上自动跑（独立 `add-android-ci` spec —— 需要 GitHub Actions emulator runner）
- 把 Android tests 接进 `./scripts/test-all.sh` 默认 GREEN（emulator boot 时间长 + 依赖大，不进默认）

## Open Questions

- [ ] **emulator boot 等待策略**：`test.sh` 内 `adb wait-for-device + getprop sys.boot_completed`，typical ~60s。OK?
- [ ] **AVD `z42_pixel6_api34`**：installer 已经创建好。test.sh 假设它存在；缺失时报错让用户重跑 installer。OK?
- [ ] **AGP vs Gradle version**：AGP 8.6 + Gradle 8.7（installer 装的）。本机已验证 AAR build 通；instrumented test 也会沿用此组合。OK?
