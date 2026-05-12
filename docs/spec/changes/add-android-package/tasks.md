# Tasks: Android per-ABI SDK package

> 状态：🟡 进行中 | 创建：2026-05-13 | 类型：refactor / build infra

## 进度概览

- [x] 阶段 1: spec 文档
- [ ] 阶段 2: examples/embedding/hello_c/README.md.android 模板
- [ ] 阶段 3: package_helpers.sh 扩展（pkg_emit_kotlin_sources / pkg_emit_jni_bridge / Android manifest variant）
- [ ] 阶段 4: platforms/android/build.sh 加 staticlib emit + export per-ABI package
- [ ] 阶段 5: scripts/package.sh 识别 `--rid android-*` dispatch
- [ ] 阶段 6: 验证 4 个 ABI 产包；任选一 ABI emulator + ndk-build hello_c smoke
- [ ] 阶段 7: README + archive + commit

## 备注

- 4 个 ABI 都在白名单（memory `project_supported_platforms`）
- AAR multi-ABI 容器进 Phase 2 Deferred
- Kotlin facade source + JNI bridge cp 进每 ABI 包（per ABI 副本）；SHA-256 invariant 校
- 与 add-android-tests in-repo AAR 流程共存
