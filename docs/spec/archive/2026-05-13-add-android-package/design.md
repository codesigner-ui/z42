# Design: Android per-ABI SDK package

## Architecture

```
platforms/android/build.sh
    ├─ cargo ndk -t arm64-v8a   → arm64-v8a/libz42_platform_android.so
    ├─ cargo ndk -t armeabi-v7a → armeabi-v7a/libz42_platform_android.so
    ├─ cargo ndk -t x86_64      → x86_64/libz42_platform_android.so
    └─ cargo ndk -t x86         → x86/libz42_platform_android.so
                          │
                          ▼ + cargo rustc --crate-type=staticlib per ABI → .a
                          │
                          ▼
scripts/package.sh --rid android-<abi>
                          ▼ pkg_emit_kotlin_sources / pkg_emit_jni_bridge
                          ▼ pkg_emit_examples_hello_c (README.md.android)
                          ▼ pkg_emit_manifest (android variant)
                          ▼ pkg_sha256_check
                          │
                          ▼
artifacts/packages/z42-<v>-android-<abi>-<config>/
```

## Decisions

### D1: per-ABI package 都装一份 Kotlin / JNI 源

每包 ~320KB Kotlin + cpp 源副本（×4 ABI = ~1.3MB 冗余）。可接受 —— 用户拿任一 ABI 包都能独立 build。SHA-256 跨包校。

### D2: 不在 per-ABI package 内放 AAR

AAR 是 multi-ABI 容器，per-ABI 包内放 AAR 不合理（容器内只一个 ABI 的 .so 反而比裸 .so 还大）。multi-ABI AAR convenience 包进 Phase 2 Deferred。

per-ABI package 的消费场景：
- CMake / ndk-build 直接 link `native/libz42_platform_android.{so,a}`
- 用户自己组合多 ABI（手动 cp 多个 package 的 .so 到一个 jniLibs/<abi>/）

### D3: build.sh 与 platforms/android in-repo AAR 流程共存

`platforms/android/build.sh` 仍产 in-repo `z42vm-release.aar`（add-android-tests 用）；通过新增 `--export-package` flag 或环境变量同时导出 4 per-ABI package。

### D4: pom.xml.template 推迟到 Phase 2 multi-ABI AAR 包

per-ABI maven coords 没有正规约定；用户用 per-ABI 包时直接 ndk-build / CMake 链接，不用 Maven。等 multi-ABI AAR convenience 包出来再附 pom.xml。

## Implementation Notes

- per-ABI `libz42_platform_android.a`：cargo-ndk 默认产 cdylib；加 `cargo rustc --crate-type=staticlib --target <android-target>` 产静态库
- ABI naming map: `android-arm64`→`arm64-v8a` / `android-armv7`→`armeabi-v7a` / `android-x64`→`x86_64` / `android-x86`→`x86`

## Testing Strategy

- 4 个 `./scripts/package.sh release --rid android-<abi>` 各跑通；目录结构 + manifest + SHA invariant
- 任选一个 ABI 包用 ndk-build 验 hello_c 链接 + 跑（emulator 上）
- `./scripts/test-all.sh` 不退步
- add-android-tests `./test.sh` 不退步
