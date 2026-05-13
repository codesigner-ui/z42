# Tasks: 移除 Android 32-bit ABI 支持

> 状态：🟢 已完成 | 创建：2026-05-13 | 归档：2026-05-13 | 类型：refactor / build infra

**变更说明：** 把 Android 支持 ABI 从 4 缩到 2，移除 `android-armv7` (armeabi-v7a) + `android-x86`。Phase 1 包矩阵从 11 RID 降到 **9 RID**。

**原因：**

- Google Play 自 2019 起要求所有 app 必须含 64-bit 原生库；arm64-v8a 是当前主流设备唯一可用 ABI
- armeabi-v7a 仅支持 Android 4.0~12 的 32-bit ROM 设备，2026 年市占率已极低
- x86 ABI 仅模拟器 + 极少 Chromebook 用，x86_64 完全覆盖
- 缩减 ABI = CI build matrix 缩短一半；维护负担同步下降

**文档影响：**

- memory `project_supported_platforms.md`：4 ABI → 2 ABI；总 RID 11 → 9
- `docs/design/runtime/embedding.md`：Phase 1 矩阵表
- `docs/workflow/building/android.md`：rustup target / ABI 列表
- `src/toolchain/host/platforms/README.md` + `src/toolchain/host/platforms/android/README.md`：AAR 描述

## 进度

- [x] 1.1 versions.toml: `[platform.android].rust_targets` / `.abis` 去掉 armv7 + x86 / i686
- [x] 1.2 scripts/_lib/package_helpers.sh: rid_to_cargo / rid_to_android_abi / validate_rid_supported_on_host / 顶部注释
- [x] 1.3 scripts/package.sh: --help / usage 文本
- [x] 1.4 src/toolchain/host/platforms/android/z42vm/build.gradle.kts: abiFilters 去掉 armeabi-v7a + x86
- [x] 1.5 src/toolchain/host/platforms/android/README.md: rustup target list
- [x] 1.6 src/toolchain/host/platforms/README.md: AAR 描述
- [x] 1.7 docs/workflow/building/android.md: rustup target / ABI 列表
- [x] 1.8 docs/design/runtime/embedding.md: Phase 1 矩阵表 4→2 + 11→9
- [x] 1.9 memory project_supported_platforms.md: 去掉 2 行 + 更新 11→9
- [x] 1.10 验证 android-arm64 + android-x64 仍能正确产包；android-armv7 / android-x86 被拒绝
- [x] 1.11 archive + commit

## 备注

- 不动 archived spec（`docs/spec/archive/2026-05-13-add-android-package/` 等）—— 它们保存的是落地当时的 4 ABI 历史
- pre-1.0 不留兼容（feedback：z42 当前不为旧版本提供兼容）—— `--rid android-armv7` 直接报错"not in whitelist"，不留 deprecation 期
- Android 32-bit drop 不影响其他平台
