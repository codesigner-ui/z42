# Platform: Android — build & run requirements

> **状态**：📋 设计期。spec：[`docs/spec/changes/add-platform-android/`](../../spec/changes/add-platform-android/)。
> **共同前置**：[`README.md`](README.md)。
>
> 本文档先把"Android facade 在你机器上跑起来需要什么"写清楚，让后续 spec 实施可以按这个清单走。落地完成后状态改 🟢，路径补 facade README 链接。

把 z42 VM 编进 Android app 的 AAR 模块，让 Kotlin / Compose 应用一行 `import io.z42.vm.Z42VM` 跑 `.zbc` 字节码。

## 工具链清单

| 工具 | 版本 | 用途 | 安装 |
|------|------|------|------|
| **Android SDK** | API 34（compileSdk）+ API 23（minSdk） | Android 平台 SDK | Android Studio 或 `commandlinetools` |
| **Android NDK** | 26.x（r26） | C/C++ 跨编 + JNI | Android Studio → SDK Manager → NDK (Side by side) |
| `cargo-ndk` 3.x | — | cargo + NDK 桥接 | `cargo install cargo-ndk` |
| Rust + 4 个 android target | — | cross-compile | `rustup target add aarch64-linux-android armv7-linux-androideabi x86_64-linux-android i686-linux-android` |
| **JDK** 17+ | — | Gradle 必需 | `brew install --cask temurin` 或 SDKMAN |
| **Gradle** 8.x | — | 构建 AAR | Android Studio 自带 wrapper；CLI 用 `./gradlew` |
| Kotlin 1.9+ | — | facade 源 | 随 Android Studio 自带 |
| `dotnet` 8.0+ | — | 编 z42 stdlib | https://dotnet.microsoft.com/download |

Android 开发可在 **macOS / Linux / Windows** 主机做。Android Studio 跨平台。

## 一次性环境准备

```bash
# 1. JDK（Gradle 必需）
brew install --cask temurin                  # macOS
# 或：apt install openjdk-17-jdk

# 2. Android Studio（自带 SDK + NDK + Gradle）
# https://developer.android.com/studio
# 启动后在 SDK Manager 装：
#   - Android SDK Platform 34
#   - Android NDK (Side by side) 26.x
#   - Android SDK Build-Tools 34.x
#   - Android SDK Command-line Tools

# 3. 环境变量（加到 ~/.zshrc / ~/.bashrc）
export ANDROID_HOME="$HOME/Library/Android/sdk"     # macOS 默认路径
export ANDROID_NDK_HOME="$ANDROID_HOME/ndk/<version>"
export PATH="$PATH:$ANDROID_HOME/cmdline-tools/latest/bin:$ANDROID_HOME/platform-tools"

# 4. Rust android targets
rustup target add aarch64-linux-android armv7-linux-androideabi x86_64-linux-android i686-linux-android

# 5. cargo-ndk（cross-compile + ndk wrapper）
cargo install cargo-ndk

# 6. 编译器 + stdlib
dotnet build src/compiler/z42.slnx
```

## 平台 feature 与 native interop

Android feature preset：`android = ["interp-only", "aot", "native-interop"]`。

- `interp-only`：Android app 沙箱内 JIT 不可用（policy + ART 冲突）；用 interp
- `aot`：AOT 占位
- `native-interop`：Android 有 `dlopen` + libffi（Bionic libc 提供），可走 Tier 1 native 注册

## 构建步骤（spec 落地后启用）

```bash
cd src/toolchain/host/platforms/android
./build.sh
```

预期 `build.sh` 内做的事：

1. fail-fast 检查 `$ANDROID_NDK_HOME` 设了 + `cargo-ndk` 在 PATH + 4 个 android target 装好
2. 编译 `<demo>.z42` → `.zbc`（如 z42c 可用）
3. 从 `artifacts/z42/libs/*.zpkg` 复制到 `z42vm/src/main/assets/stdlib/`
4. `cargo ndk -t arm64-v8a -t armeabi-v7a -t x86_64 -t x86 -o z42vm/src/main/jniLibs build --release --manifest-path rust/Cargo.toml`
5. `./gradlew :z42vm:assembleRelease` 打 `.aar`

构建后产物：

```
src/toolchain/host/platforms/android/
├── z42vm/build/outputs/aar/z42vm-release.aar
└── z42vm/src/main/jniLibs/
    ├── arm64-v8a/libz42_platform_android.so
    ├── armeabi-v7a/libz42_platform_android.so
    ├── x86_64/libz42_platform_android.so
    └── x86/libz42_platform_android.so
```

`.aar` 与 `jniLibs/*.so` 都不入 git；CI 在 release 上传。

## 在 Android 项目里消费

App 的 `build.gradle.kts`：

```kotlin
dependencies {
    // 本地文件依赖（开发期）：
    implementation(files("path/to/z42vm-release.aar"))
    // 或者一旦发到 Maven repo：
    // implementation("io.z42:z42vm:0.1.0")
}
```

Kotlin 用法（设计期 sketch）：

```kotlin
import io.z42.vm.Z42VM
import io.z42.vm.AssetZpkgResolver

val vm = Z42VM(zpkgResolver = AssetZpkgResolver(assets))
vm.stdoutHandler = { bytes -> textView.append(String(bytes)) }

val zbcBytes = assets.open("hello.zbc").readBytes()
vm.use {
    val m = it.loadZbc(zbcBytes)
    val e = it.resolveEntry(m, "App.Main")
    it.invoke(e)
}
```

详细 API 形态由 `add-platform-android` spec design.md 锁定。

## 验证（spec 落地完成的 GREEN 标准）

| 检查 | 命令 |
|------|------|
| Rust × 4 ABI | `cargo ndk -t arm64-v8a -t armeabi-v7a -t x86_64 -t x86 build --release --manifest-path src/toolchain/host/platforms/android/rust/Cargo.toml` |
| Gradle 检查 | `cd src/toolchain/host/platforms/android && ./gradlew :z42vm:assembleRelease` |
| AAR 产物 | `ls z42vm/build/outputs/aar/z42vm-release.aar` |
| 既有 lib 测试不退化 | `cargo test --manifest-path src/runtime/Cargo.toml --lib host::` ≥ 22 通过 |

## 推迟到独立 spec 的内容

- **AndroidDemo Compose app**：归 `add-platform-android-demo`
- **JUnit / instrumented tests**：归 `add-platform-android-tests`
- **just / GitHub Actions ubuntu-latest CI job**：归 `add-platform-android-ci`

## 故障排查

| 现象 | 处理 |
|------|------|
| `cargo: command not found: ndk` | `cargo install cargo-ndk` |
| `error: linker not found for target aarch64-linux-android` | `$ANDROID_NDK_HOME` 未设或值错；echo 检查后修 |
| `Could not find tool: aarch64-linux-android21-clang` | NDK 版本与 cargo-ndk 不匹配；升级 NDK 到 r26+ |
| Gradle `Could not resolve all dependencies` | JDK 未装或版本 < 17 |
| `Permission denied: gradlew` | `chmod +x ./gradlew` |
| `UnsatisfiedLinkError: dlopen failed: library "libz42_platform_android.so" not found` | jniLibs 未编出或 ABI 不匹配；检查 `z42vm/src/main/jniLibs/<abi>/` 是否含正确 ABI |
| `libffi-sys` build 失败 | Android NDK 自带 libffi headers；cargo-ndk 应自动接通。如失败检查 `LIBFFI_SYS_USE_PKG_CONFIG=0` |
| stdlib 找不到 zpkg | `assets/stdlib/` 没拷进 AAR；检查 `build.sh` 是否成功复制 |

## 关联文档

- 设计与决策：[`docs/spec/changes/add-platform-android/`](../../spec/changes/add-platform-android/)
- 跨平台契约：[`platforms/README.md`](../../../src/toolchain/host/platforms/README.md)
- Embedding API：[`docs/design/runtime/embedding.md`](../../design/runtime/embedding.md)
- cross-platform.md：[`docs/design/runtime/cross-platform.md`](../../design/runtime/cross-platform.md) Android 段
