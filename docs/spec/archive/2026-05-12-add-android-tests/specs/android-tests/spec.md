# Spec: Android facade JUnit instrumented tests (android-tests)

> 实现 [`platform-test-contract`](../../../../archive/2026-05-12-define-platform-test-contract/specs/platform-test-contract/spec.md) R1–R7。本 spec 只描述 Android / JUnit 落地形态；scenario 语义见 contract spec。

## 平台执行环境

- **AVD**：`z42_pixel6_api34`（Pixel 6 / API 34 / arm64-v8a Google Play system image）
- **Emulator**：`$ANDROID_HOME/emulator/emulator` headless（`-no-window -no-audio -gpu swiftshader_indirect`）
- **Build**：Gradle 8.7 + AGP 8.6（本地 Gradle 装 `artifacts/tools/gradle/gradle-8.7/`）
- **NDK**：r26 在 `artifacts/tools/android-ndk/`（cargo-ndk 用）
- **Test runner**：`androidx.test:runner` + AndroidJUnit4

## scenario → JUnit test 方法映射

| 契约 # | JUnit 方法 | 说明 |
|--------|------------|------|
| R1 | `testSmokeHelloWorld` | load `hello.zbc` → invoke `Hello.Main` → captured stdout == `"hello, world\n"` |
| R2 | `testBadZbcThrowsBadZbc` | `vm.loadZbc(byteArrayOf(0xDE, ...))` → `Z42VMException` status 10 |
| R3 | `testResolveUnknownEntryThrowsEntryNotFound` | `vm.resolveEntry("App.Ghost")` → status 20 |
| R4 | `testInvokeWrongArgCountThrowsArgMismatch` | invoke with extra arg → status 21 |
| R5 | `testMapResolverWithoutCorelibSurfacesAtInvoke` | `MapZpkgResolver(...)` 仅含 Std.Phantom → status 10/30 |
| R6 | `testInitShutdownLifecycleRoundtrip` | `.use { ... }` × 3 轮跑 R1 smoke 全过 |
| R7 | `testMultiLineStdoutPreservesOrder` | load `multi_line.zbc` → 字节累积 == `"a\nb\nc\n"` |

## ADDED Requirements

### Requirement 1: build.sh 编 fixtures 到 androidTest/assets/

`./build.sh` 把 `examples/embedding/{hello,multi_line}.z42` 编出 `.zbc` 拷到 `z42vm/src/androidTest/assets/test-fixtures/`，与 add-ios-tests 共享同一份 fixture 源。

#### Scenario: build.sh 产 test fixtures
- **WHEN** `./build.sh` 跑完
- **THEN** `z42vm/src/androidTest/assets/test-fixtures/hello.zbc` + `multi_line.zbc` 存在

### Requirement 2: ./test.sh 在 emulator 上跑通 R1–R7

`./test.sh` 启动 AVD 在后台，等 boot complete，跑 `./gradlew :z42vm:connectedAndroidTest`，7/7 全过。

#### Scenario: test.sh 全 7 个 instrumented test 绿
- **WHEN** AVD `z42_pixel6_api34` 存在，运行 `./test.sh`
- **THEN** Gradle 输出 `BUILD SUCCESSFUL`，emulator 上 7 个 instrumented test 全过

### Requirement 3: build.sh 修真实 bug

修复 cargo ndk cwd + cpp 转发头 `../` 数错两处 grandfather bug。

#### Scenario: build.sh on clean env succeeds end-to-end
- **WHEN** clean clone + 装好 Android toolchain（`./scripts/install-android-toolchain-local.sh`）后跑 `./build.sh`
- **THEN** AAR `z42vm/build/outputs/aar/z42vm-release.aar` 产出，4 ABI cdylib + JNI .so 全部进 jniLibs/

### Requirement 4: gradlew 提交入仓

`gradlew` + `gradle/wrapper/gradle-wrapper.{jar,properties}` 由本地 Gradle 8.7 generate，提交入仓，下游用户 `./gradlew ...` 不需要先装 system gradle。

#### Scenario: gradlew 可独立执行
- **WHEN** 仓库 clone 后直接 `cd platforms/android && ./gradlew --version`
- **THEN** 输出 Gradle 8.7（首次会下载 distribution 到 `GRADLE_USER_HOME`）

## MODIFIED Requirements

### Requirement: Android README Limitations 段移除 "JUnit 推迟"

**Before:** Limitations 含 `**Demo / JUnit / CI**：推迟到独立 spec（add-platform-android-demo / -tests / -ci）`

**After:** 只保留 `**Demo / CI**：推迟到独立 spec（-demo / -ci）`；JUnit 在本 spec 落地

### Requirement: embedding.md §11.7 Android 行已支持 namespace index

**Before:** Android 行：`AssetZpkgResolver(context.assets)` 读 `assets/stdlib/<ns>.zpkg`

**After:** `AssetZpkgResolver(context.assets, "stdlib")` 先查 `assets/stdlib/index.json` 把 namespace map 到 zpkg 文件名，再读 `assets.open("$subdir/$filename")`；index 缺失或 miss 时回退到 `<ns>.zpkg`（已在 `fix-bundle-resolver-namespace-index` 落地，本 spec 在 README + design 同步对齐）

## Pipeline Steps

不涉及编译器 pipeline。
