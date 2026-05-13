# Tasks: Android per-ABI SDK package

> 状态：🟢 已完成 | 创建：2026-05-13 | 归档：2026-05-13 | 类型：refactor / build infra

## 进度概览

- [x] 阶段 1: spec 文档
- [x] 阶段 2: examples/embedding/hello_c/README.md.android 模板
- [x] 阶段 3: package_helpers.sh 扩展（pkg_emit_kotlin_sources / pkg_emit_jni_bridge / Android manifest variant / SHA-256 cross-checks）
- [x] 阶段 4: scripts/_lib/package_android.sh（cargo ndk so + cargo rustc staticlib + facade + manifest）
- [x] 阶段 5: scripts/package.sh `--rid android-*` dispatch（在 Phase 1.2 已落地）
- [x] 阶段 6: 验证 4 个 ABI 产包；ELF 架构正确；SHA 跨包 invariant pass
- [x] 阶段 7: archive + commit

## 备注

- 4 个 ABI 都在白名单（memory `project_supported_platforms`）
- AAR multi-ABI 容器进 Phase 2 Deferred
- Kotlin facade source + JNI bridge cp 进每 ABI 包（per ABI 副本）；SHA-256 invariant 跨 4 ABI 校通过
- 与 add-android-tests in-repo AAR 流程共存
- cargo-ndk 不喜欢 `--crate-type=staticlib` 输出（它期望 cdylib 的 .so）；workaround
  是允许 cargo ndk 调用返回非零 + 直接校验 `.a` 存在
- 4 个包大小：arm64 (.a 9.7M, .so 776K) / armv7 (.a 7.3M, .so 512K) /
  x64 (.a 9.4M, .so 824K) / x86 (.a 6.7M, .so 808K)
- ELF 验证：arm64=ARM aarch64 / armv7=ARM EABI5 / x64=x86-64 / x86=Intel 80386
