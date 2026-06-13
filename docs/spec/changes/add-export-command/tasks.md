# Tasks: add-export-command

> 状态：🟡 进行中 | 创建：2026-06-14

**变更说明：** `z42 export ios/android/wasm <project.z42.toml>` — 从 z42 项目 toml 生成原生平台工程（iOS .xcodeproj / Android Gradle / WASM HTML+JS）。平台配置读自 `[platform.ios]`/`[platform.android]`/`[platform.wasm]` toml 段；平台 SDK 缓存到 `runtimes/<rid>/<ver>/`（复用 runtimes 目录，用 RID 子目录区分）。
**原因：** 支持 z42 应用发布到 App Store / Google Play / Web，减少手动配置参数（通过 toml 集中管理）。
**文档影响：** `docs/design/compiler/project.md`（`[platform.*]` 段）、`docs/design/toolchain/export.md`（新建）

## 进度

- [x] 1.1 ACTIVE.md 锁 toolchain
- [x] 1.2 创建 tasks.md
- [x] 2.1 `src/compiler/z42.Project/ProjectManifest.cs` — 注册 `platform` 顶层 key + 扫描 `[platform.ios/android/wasm]` 子段
- [x] 2.2 `src/toolchain/launcher/core/launcher_cli.z42` — 新增 `_exportRouter()`；`_launcherRoot` 追加 `AddRouter("export")`；`install` ArgParser 追加 `--rid`；dispatch 追加 `export`
- [x] 2.3 `src/toolchain/launcher/core/launcher_network.z42` — `_cmdInstall` 读 `--rid` 覆盖 `_hostRid()`；非宿主 RID → 安装到 `runtimes/<rid>/<ver>/`
- [x] 2.4 新建 `src/toolchain/launcher/core/launcher_export.z42` — 共享入口 + toml 读取 + SDK 路径 helpers
- [x] 2.5 新建 `src/toolchain/launcher/core/launcher_export_ios.z42` — Xcode `.xcodeproj` 生成器
- [x] 2.6 新建 `src/toolchain/launcher/core/launcher_export_android.z42` — Gradle 工程生成器
- [x] 2.7 新建 `src/toolchain/launcher/core/launcher_export_wasm.z42` — HTML+JS 工程生成器
- [x] 3.1 新建 `docs/design/toolchain/export.md`
- [x] 3.2 更新 `docs/design/compiler/project.md`（`[platform.*]` 段）
- [x] 3.3 编译验证：launcher 8/8 files 全绿（4 原有 + 4 新增），零错误，零新告警

## 备注

- `z42.launcher.z42.toml` 使用 `include = ["*.z42"]`，新文件自动纳入，无需改 toml
- 平台 SDK `runtimes/<rid>/<ver>/` 目录与桌面运行时共用 runtimes 根；RID 区分（ios-arm64/android-arm64/browser-wasm）
- 首版 SDK 若未安装：命令完成但打印 ⚠ 提示，不阻塞工程生成
- `Directory.Copy` 尚未在 stdlib：xcframework 拷贝给出 `cp -r` 提示而非自动拷贝
- `[platform.*]` 命名比 `[export.*]` 更自然：即使不导出、仅配置平台特定参数也适用
