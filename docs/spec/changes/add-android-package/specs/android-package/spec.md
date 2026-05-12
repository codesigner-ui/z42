# Spec: Android per-ABI SDK package

> 落地 4 个 Android ABI package：`android-arm64` (arm64-v8a) / `android-armv7` (armeabi-v7a) / `android-x64` (x86_64) / `android-x86` (x86)。AAR multi-ABI convenience 留 Phase 2 Deferred。

## ADDED Requirements

### Requirement 1: 4 个 per-ABI package

#### Scenario: 4 个 ABI 都产独立 package
- **WHEN** `for abi in arm64 armv7 x64 x86; do ./scripts/package.sh release --rid android-$abi; done`
- **THEN** `artifacts/packages/z42-<v>-android-{arm64,armv7,x64,x86}-release/` 4 个目录都存在

### Requirement 2: 每 ABI package 结构

```
z42-<v>-android-<abi>-<config>/
├── bin/README.md
├── libs/                                stdlib zpkg + index.json
├── native/
│   ├── libz42_platform_android.a        per-ABI 静态库
│   ├── libz42_platform_android.so       per-ABI 动态库
│   └── include/{z42_abi,z42_host}.h
├── kotlin/io/z42/vm/*.kt                Kotlin facade（cp 自 platforms/android/z42vm/src/main/java/...）
├── cpp/{z42vm_jni.c,CMakeLists.txt}     JNI bridge 源
├── examples/hello_c/{main.c,hello.zbc,README.md}
└── manifest.toml
```

#### Scenario: per-ABI native lib 是该 ABI 对应的 ELF
- **WHEN** Phase 1.3 完成
- **THEN** `z42-<v>-android-arm64-release/native/libz42_platform_android.so` 是 ELF aarch64；x64 是 ELF x86_64；以此类推

### Requirement 3: Kotlin facade source cp 进每 package

#### Scenario: kotlin/ 内容跨 4 ABI byte-identical
- **WHEN** 比较 4 个 Android package 的 `kotlin/io/z42/vm/*.kt`
- **THEN** SHA-256 完全相同

### Requirement 4: JNI bridge source cp 进每 package

#### Scenario: cpp/z42vm_jni.c + CMakeLists.txt 跨 4 ABI byte-identical
- **WHEN** 比较 4 个 Android package 的 `cpp/`
- **THEN** SHA-256 完全相同

### Requirement 5: examples/hello_c/ Android 平台

`examples/hello_c/README.md` 给 ndk-build / Gradle externalNativeBuild link 命令。

#### Scenario: hello_c README 含 ndk-build 命令
- **WHEN** 读 `z42-<v>-android-arm64-release/examples/hello_c/README.md`
- **THEN** 含 `ndk-build` 或 `cmake -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake ...` 示例

### Requirement 6: SHA-256 invariant

`libs/` + `native/include/` + `examples/hello_c/main.c` SHA-256 与所有其它 package 一致。

#### Scenario: SHA-256 check pass
- **WHEN** `pkg_sha256_check` 跑 4 个 Android package
- **THEN** 全部通过

### Requirement 7: manifest.toml Android 字段

#### Scenario: Android manifest 完整
- **WHEN** 读 `z42-<v>-android-arm64-release/manifest.toml`
- **THEN** `[package].rid = "android-arm64"` + `[compat]` 含 `android-min-sdk = 23` + `android-target-sdk = 34` + `[contents.platform]` 含 `kotlin-sources = "kotlin/io/z42/vm"`

## MODIFIED Requirements

### Requirement: platforms/android/build.sh 产物位置

**Before:** `platforms/android/build.sh` 把 AAR 产到 `z42vm/build/outputs/aar/`（in-repo 测试用）

**After:** 保留 in-repo AAR；**附加**导出 per-ABI package 到 `artifacts/packages/z42-<v>-android-<abi>-<config>/`。底层 .so / .a 通过 cargo-ndk 已经分 ABI 目录产；package.sh 直接 cp。

## Pipeline Steps

不涉及编译器 pipeline。
