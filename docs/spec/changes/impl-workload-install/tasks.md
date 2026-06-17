# Tasks: impl-workload-install (B2) — 🟡 B2-1 iOS 端到端 ✅

**变更说明：** runtime 与 workload tooling 分包（版本管理）+ `z42 workload install/list/remove`。
**锁：** `toolchain`。设计/决策见 proposal.md + design.md。

## B2-1 iOS 端到端（✅ 完成 + 本地 swift build 验证）
- [x] 1.1 xtask_package_ios 拆分：`_buildRuntimePackageIos`（z42-runtime-<ver>-<rid>：xcframework+headers+libs）+ `_packageIos`（workload tooling：Sources+Package.swift+examples+manifest）
- [x] 1.2 Package.swift binaryTarget = `__Z42_RUNTIME_XCFRAMEWORK__` 占位符（install 解析）
- [x] 1.3 Z42VMC headers **自包含**（拷真实 runtime 头，非源码树转发桩——后者 `#include "../.."` 打包后失效）
- [x] 1.4 launcher_workload.z42：`workload install <wl> --from <tooling> [--runtime <pack>] [--version] [--rid]`（递归拷 tooling+runtime + 改写 Package.swift 为**相对路径**）/ `list` / `remove`
- [x] 1.5 launcher_cli.z42：workload router + dispatch（z42.launcher.z42.toml `*.z42` glob 自动纳入）
- [x] 1.6 GREEN：launcher+xtask 清编 exit 0；**端到端**：macos-slice runtime pack + tooling → `z42 workload install ios` → Package.swift 改写 `../../../macos-arm64/0.3.0/native/Z42VM.xcframework` → **`swift build` Build complete!** + `workload list` 列出 ios

## 关键实现发现
- **SwiftPM binaryTarget 路径必须相对 package root**（非绝对）→ install 写 `../../../<rid>/<ver>/native/Z42VM.xcframework`（布局固定）。
- **Z42VMC 转发头源码树耦合**（合并包当年也只在 repo 内 build）→ 包内放自包含真实头，SwiftPM 自动生成 module map。
- z42 stdlib **无 `Array.Sort`、无 `Directory.Copy`、`IndexOf` 无 startIndex 重载** → 自写递归拷贝、手动 quote 解析。

## 余下（后续 change）
- [ ] B2-3 android/wasm 照 iOS 模式拆（NDK/wasm-pack 多归 CI）；多-ABI .so（android）vs 单容器 xcframework（ios）模型不同。
- [ ] B2-4 CI release.yml 上传 workload + runtime packs + `workload install` 走 manifest 联网。
- [ ] 真实 iOS device/sim 多-slice xcframework 合并（本次用 macos 单 slice 验机制；真机包归 CI）。
- [ ] B1 命令发现（workload 命令进 `z42 -h` 树）。
