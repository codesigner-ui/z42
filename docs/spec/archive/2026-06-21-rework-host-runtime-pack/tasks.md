# Tasks: host runtime pack = 完整可嵌入运行时（从 SDK 复制）

> 状态：🟢 已完成 | 创建：2026-06-20 改写：2026-06-21 完成：2026-06-21 | 类型：fix+refactor（toolchain packaging）

**变更说明：**
1. 桌面 `z42-runtime-<ver>-<rid>` 包从 SDK 包**逐字节复制** `libs/` + `native/`（嵌入件 + C ABI headers），使其成为**完整可嵌入运行时**（与 ios/android/wasm 的 runtime pack 对称）。此前桌面 runtime 包只有 `z42vm + libs`，无 native/，桌面嵌入用户被迫下整个 SDK。
2. 顺带修桌面 **SDK native/ 误装 42M `libz42_platform_ios.a`** 的 bug：`_copyNativeLibs` 之前 `libz42*` 通配把移动平台 facade（残留在共享 cargo target dir）一并拷进了桌面 SDK。

**原因：** User 裁决（2026-06-20）runtime 包应是完整可嵌入运行时；User 进一步提出（2026-06-21）host runtime pack 应直接从 SDK 复制 libs+native（DRY、单一字节源），本变更采纳该方案。

**文档影响：** runtime-workload-distribution.md 第 85 行 runtime 包内容定义已同步。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/xtask_package.z42` | MODIFY | `_copyNativeLibs` 排除 `libz42_platform_*`；`_buildPackageCore` 传 SDK pkgDir 给 `_buildRuntimePackage` |
| `scripts/xtask_package_desktop.z42` | MODIFY | `_buildRuntimePackage` 改签名收 sdkPkgDir，`Directory.Copy` 从 SDK 拷 libs/ + native/ |
| `docs/design/toolchain/runtime-workload-distribution.md` | MODIFY | runtime 包内容定义同步 |

**只读引用：**
- `src/libraries/z42.io/src/Directory.z42` — `Directory.Copy(src,dst,recursive)`（复用）

## 任务
- [x] 1.1 `_copyNativeLibs`：`continue` 跳过 `libz42_platform_*`（修 SDK 误装 42M ios .a）
- [x] 1.2 `_buildRuntimePackage`：签名加 sdkPkgDir；`Directory.Copy` 从 SDK 拷 libs/ + native/；更新头注释
- [x] 1.3 `_buildPackageCore` desktop 分支：`_buildRuntimePackage(root, pkgDir, ...)`
- [x] 1.4 docs：runtime-workload-distribution.md 第 85 行
- [x] 2.1 重建 xtask 全量编译无 error
- [x] 2.2 `package release --rid macos-arm64`：SDK native/ **无** `libz42_platform_ios.a`（瘦 ~44M）；runtime 包含 `native/{libz42.{a,dylib},libz42_compression.{a,dylib},include/{z42_abi,z42_host}.h}` + `libs/`(52) + `z42vm`；与 SDK 对应 5 文件 `cmp` **byte-identical**；SHA-256 invariant ✓
- [x] 2.3 `test dist`：377 passed / 0 failed（interp+jit）

## 备注
- `_copyNativeLibs` 仅 desktop 用（已确认）→ 排除 platform facade 安全；移动包走各自 `_buildRuntimePackage{Android,Ios}` 显式拷 platform .so/.a，不受影响。
- runtime 包 native/ 自动继承 SDK 的 clean native/（含 ios .a 修复）→ 不会被 42M 污染。
