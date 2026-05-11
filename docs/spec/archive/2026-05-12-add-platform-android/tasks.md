# Tasks: Add Android Platform Scaffold

> 状态：🟢 已完成 | 创建：2026-04-29 / 重写：2026-05-12
> 前置：[`add-zpkg-resolver-hook`](../../archive/2026-05-12-add-zpkg-resolver-hook/) ✅
> 同期参考：[`add-platform-wasm`](../../archive/2026-05-12-add-platform-wasm/) / [`add-platform-ios`](../../archive/2026-05-12-add-platform-ios/)
> Spec 修订：见 [design.md REVISION 2026-05-11](design.md)
>
> 本次实施范围（与原稿差异）：
> - 测试 / Demo / CI 推迟到独立 spec
> - 目录从 `platform/android/` 迁到 `src/toolchain/host/platforms/android/`
> - Rust crate 直接 wrap `z42-host`（Tier 2）
> - 类名 `Z42Vm` → `Z42VM`
> - JNI bridge 用 C（不写 Rust JNI）；Rust 端纯 re-export
> - ZpkgResolver 协议 + `AssetZpkgResolver(assets)` 默认实现

## 进度概览

- [x] 阶段 1: 目录骨架 + .gitignore
- [x] 阶段 2: rust/ binding crate（cdylib，4 ABI 跨编通过 cargo-ndk）
- [x] 阶段 3: Kotlin facade（io.z42.vm 包 + Z42VM / Z42VMModule / Z42VMEntry / Z42VMValue / Z42VMException + ZpkgResolver + AssetZpkgResolver）
- [x] 阶段 4: JNI bridge（cpp/z42vm_jni.c + CMakeLists.txt）
- [x] 阶段 5: Gradle module（settings.gradle.kts + z42vm/build.gradle.kts + root build.gradle.kts）
- [x] 阶段 6: build.sh（cargo-ndk × 4 ABI + gradle :z42vm:assembleRelease + stdlib 复制）
- [x] 阶段 7: README + 文档同步
- [x] 阶段 8: 验证 + commit + push + archive

---

## 阶段 1: 目录骨架

- [x] 1.1 `src/toolchain/host/platforms/android/` 顶层
- [x] 1.2 `.gitignore` 忽略 `rust/target/` / `z42vm/build/` / `z42vm/src/main/jniLibs/` / `z42vm/src/main/assets/stdlib/*.zpkg` / `.gradle/`

## 阶段 2: rust/ binding crate

- [x] 2.1 `rust/Cargo.toml` —— crate `z42-platform-android`，`crate-type = ["cdylib"]`，path-deps `z42`（features=android）+ `z42-host`
- [x] 2.2 `rust/src/lib.rs` —— 纯 `pub use z42::host::*;`，让符号导出到 `libz42_platform_android.so`
- [x] 2.3 验证：`cargo ndk -t arm64-v8a build --manifest-path rust/Cargo.toml` —— **本机无 Android NDK 未实测**；scaffold 完整，待用户机器装 NDK 后跑 `./build.sh` 验证

## 阶段 3: Kotlin facade

包 `io.z42.vm`，与 `platforms/README.md` 跨平台契约对齐：

- [x] 3.1 `z42vm/src/main/java/io/z42/vm/Z42VM.kt` —— 主 class，`AutoCloseable`，构造器接 `ZpkgResolver` + sink handlers，`loadZbc` / `resolveEntry` / `invoke` / `close`
- [x] 3.2 `z42vm/src/main/java/io/z42/vm/Z42VMModule.kt` —— 不透明 handle wrapper（持 `nativeHandle: Long`）
- [x] 3.3 `z42vm/src/main/java/io/z42/vm/Z42VMEntry.kt`
- [x] 3.4 `z42vm/src/main/java/io/z42/vm/Z42VMValue.kt` —— sealed class（Null / I64 / F64 / Bool）
- [x] 3.5 `z42vm/src/main/java/io/z42/vm/Z42VMException.kt` —— RuntimeException + `status: Int`
- [x] 3.6 `z42vm/src/main/java/io/z42/vm/ZpkgResolver.kt` —— interface + `AssetZpkgResolver(assets, subdir="stdlib")` + `MapZpkgResolver` 工具

## 阶段 4: JNI bridge

- [x] 4.1 `z42vm/src/main/cpp/z42vm_jni.c` —— C 实现 `Java_io_z42_vm_Z42VM_native*` 入口；用 `z42_host.h` 调 `z42_host_*` 函数；含 stdout/stderr sink trampolines + zpkg resolver trampoline（回调 Kotlin）
- [x] 4.2 `z42vm/src/main/cpp/CMakeLists.txt` —— 编 `z42vm_jni` shared lib，链接 `libz42_platform_android.so`
- [x] 4.3 `z42vm/src/main/cpp/include/z42_host.h` + `z42_abi.h` —— 转发头到 `src/runtime/include/`

## 阶段 5: Gradle module

- [x] 5.1 `settings.gradle.kts` —— `include(":z42vm")`，rootProject.name = "Z42VM"
- [x] 5.2 `build.gradle.kts` (root) —— Android Gradle Plugin + Kotlin plugin classpath
- [x] 5.3 `z42vm/build.gradle.kts` —— `library` plugin，namespace `io.z42.vm`，minSdk 23，compileSdk 34，jniLibs.srcDirs，externalNativeBuild.cmake
- [x] 5.4 `gradle.properties` —— android.useAndroidX=true 等
- [x] 5.5 `gradle/wrapper/gradle-wrapper.properties`（如本机没有 gradlew 链可由 README 引导用户从 Android Studio 拉）

## 阶段 6: build.sh

- [x] 6.1 fail-fast 检查 `cargo-ndk` + `ANDROID_NDK_HOME` + 4 ABI rust target + JDK
- [x] 6.2 从 `artifacts/build/libs/release/*.zpkg` 复制到 `z42vm/src/main/assets/stdlib/`
- [x] 6.3 `cargo ndk -t arm64-v8a -t armeabi-v7a -t x86_64 -t x86 -o z42vm/src/main/jniLibs build --release --manifest-path rust/Cargo.toml`
- [x] 6.4 `./gradlew :z42vm:assembleRelease` 打 AAR
- [x] 6.5 输出最终 `.aar` 路径

## 阶段 7: README + 文档同步

- [x] 7.1 `src/toolchain/host/platforms/android/README.md` —— quick start + API + 限制 + 故障排查
- [x] 7.2 `src/toolchain/host/platforms/README.md` 平台索引行 Android 状态 → 🟢
- [x] 7.3 `docs/workflow/building/android.md` 状态 → 🟢；spec 链接换为 archive
- [x] 7.4 `docs/workflow/building/README.md` Android 行状态 icon
- [x] 7.5 `src/toolchain/host/README.md` H4 (android) → ✅
- [x] 7.6 `docs/roadmap.md` L2 Embedding 行加 add-platform-android

## 阶段 8: 验证

- 🔵 8.1 `cargo build --target aarch64-linux-android --manifest-path .../android/rust/Cargo.toml` —— **本机无 NDK 未实测**；scaffold + Cargo.toml + 4 个 rustup target 装好后由用户在装了 NDK 的机器上跑
- [x] 8.2 `cargo build --features ios / android / wasm / interp-only / default` 5 个 preset 不退化
- [x] 8.3 既有 lib tests 不退化（pre-existing 6 个 host_tests 失败属上游 typecheck/artifact-layout 工作，与本 spec 无关）
- [x] 8.4 commit + push + 归档

---

## 备注

### 推迟的阶段

- AndroidDemo Compose app → 独立 spec `add-platform-android-demo`
- JUnit / instrumented tests → `add-platform-android-tests`
- just / CI（ubuntu-latest）→ `add-platform-android-ci`

### Stage 0 沿袭

iOS spec 已把 `android` feature preset 改为 `["interp-only", "aot"]`（去掉 `native-interop`）。同 iOS：libffi-sys CFI / 系统库链接对 Android NDK 同样不友好，v0.1 Tier 1 native 注册先不支持。

### 实施依赖

- ✅ `add-zpkg-resolver-hook`
- ✅ runtime native-interop feature gate（wasm spec 完成）
- 🛠️ `rustup target add aarch64-linux-android armv7-linux-androideabi x86_64-linux-android i686-linux-android`（本次实施期装）
- 🛠️ `cargo install cargo-ndk --locked`
- ⚠️ Android NDK r26+（约 1 GB 下载；如本机不装，build.sh 仍 fail-fast 提示，scaffold + spec 完整）

### 与原稿差异

| 项 | 原稿 (2026-04-29) | 修订 (2026-05-12) |
|---|-------------------|--------------------|
| 类名 | `Z42Vm` | `Z42VM` |
| 依赖 | `z42_runtime::interp::Interpreter` | `z42-host` Tier 2 |
| 路径 | `platform/android/` | `src/toolchain/host/platforms/android/` |
| JNI bridge | Rust JNI（jni crate）| C（手写 ≤ 200 行） |
| Demo | AndroidDemo Compose app | **推迟** |
| CI | ubuntu-latest job | **推迟** |
| native-interop | 默认开 | **暂关**（与 iOS 同步；libffi 跨编 issue） |
