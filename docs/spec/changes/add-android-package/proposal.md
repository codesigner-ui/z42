# Proposal: Android SDK package（4 per-ABI）

## Why

`define-package-layout` (Phase 1.0) 契约要求 Android 端出 4 个 per-ABI SDK package（对齐 memory `project_supported_platforms` 全 4 ABI 白名单）：

- `z42-<v>-android-arm64-<config>`  (arm64-v8a)
- `z42-<v>-android-armv7-<config>`  (armeabi-v7a)
- `z42-<v>-android-x64-<config>`    (x86_64)
- `z42-<v>-android-x86-<config>`    (x86)

当前 `platforms/android/build.sh` 产 `z42vm-<v>.aar`（multi-ABI 容器），但**没有**按 per-arch flat 契约导出。本 spec 让 `scripts/package.sh --rid android-<abi>` 各产一份 per-ABI package。

## What Changes

1. `scripts/package.sh` 加 Android handling
2. `scripts/_lib/package_helpers.sh` 扩展：`pkg_emit_kotlin_sources` / `pkg_emit_jni_bridge` / `pkg_emit_android_pom_template` / `pkg_emit_examples_hello_c_android`
3. `platforms/android/build.sh` 重构：cargo-ndk 产物拆出 per-ABI 单文件 .so/.a；额外导出 per-ABI package 到 `artifacts/packages/`
4. `examples/embedding/hello_c/README.md.android` —— ndk-build / Gradle externalNativeBuild link 命令
5. SHA-256 invariant：libs/ + native/include/ + examples/hello_c/main.c 跨 11 包一致

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/package.sh`                                                          | MODIFY | `--rid android-*` dispatch |
| `scripts/_lib/package_helpers.sh`                                             | MODIFY | Android-specific helpers |
| `src/toolchain/host/platforms/android/build.sh`                               | MODIFY | export per-ABI package（与 in-repo AAR 流程共存）|
| `examples/embedding/hello_c/README.md.android`                                | NEW    | Android link 指南 |
| `docs/spec/changes/add-android-package/{proposal,design,tasks}.md`            | NEW    | 本 spec |
| `docs/spec/changes/add-android-package/specs/android-package/spec.md`         | NEW    | Requirement 列表 |

## Out of Scope

- 多 ABI 卷的 AAR convenience 包（Phase 2 Deferred）
- Maven publish 真发（Phase 4）
- Android signing keystore（Phase 4）

## Open Questions

- [ ] **per-ABI package 含 Kotlin facade 副本**：4 个 ABI package 各装一份 Kotlin .kt（per ABI 副本，~80KB × 4 = ~320KB 冗余）。值不值？我倾向 **是**，每 package 自包含（独立 install 也能用）；SHA-256 invariant 跨 4 ABI 同步校。
- [ ] **JNI bridge .c 源**：cpp/z42vm_jni.c + CMakeLists.txt 同上 cp 进每 package；用户自己 ndk-build 时直接拿。OK?
- [ ] **pom.xml.template 是否每 package 都放**：Maven coords 不带 ABI 维度（一个 z42vm:0.1.0 maven artifact 在 multi-ABI AAR 内）。per-ABI package 的 pom.xml 用什么 coords？我倾向 `io.z42:z42vm-<abi>:<v>` per ABI 命名，或留空 + README 说明"等 multi-ABI AAR 包出来再用 maven"。
