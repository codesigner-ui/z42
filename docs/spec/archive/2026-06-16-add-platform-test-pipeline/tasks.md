# Tasks: add-platform-test-pipeline

> 状态：🟢 已完成 | 创建：2026-06-15 | 完成：2026-06-16 | 类型：refactor + docs（toolchain）

**变更说明：** 三平台（wasm/iOS/Android）测试统一到接口驱动的 z42 框架，三步
（①构建工程 ②构建测试资产 ③跑测试）清晰可单独调用，共享②逻辑只写一份。
**原因：** 现状三套各自为政 bash，②逻辑三处重复易漂移，未接入 xtask gate。
**文档影响：** design.md（架构）+ cross-platform.md（R1–R7 契约 + 实现原理）。
**并行：** 占 toolchain；User 多次授权与 migrate-scripts-to-z42 / port-z42c-core 并行。

> 设计已 User 确认（接口驱动 + 三平台后端一次性，2026-06-15）。Scope 见 proposal.md。

## 进度概览
- [x] 阶段 1: 共享框架（interface + 驱动 + 注册表 + 共享 assets）
- [x] 阶段 2: 三平台后端
- [x] 阶段 3: CLI 接线 + 注册
- [x] 阶段 4: 验证 + 文档 + 归档

## 阶段 1: 共享框架
- [x] 1.1 `xtask_test_platform.z42`：`IPlatformBackend` 接口 + `AssetLayout` class
- [x] 1.2 共享 `_platformAssets`（编 fixtures + 收 stdlib + 可选 files.json）
- [x] 1.3 三步驱动 `_platformBuild`/`_platformRun`/`_platformAll` + `_backendFor` 注册表

## 阶段 2: 三平台后端
- [x] 2.1 `xtask_test_wasm.z42` WasmBackend（wasm-pack web+nodejs；落点 js/fixtures+js/stdlib+files.json；run=local node+playwright）
- [x] 2.2 `xtask_test_ios.z42` IosBackend（cargo×targets+xcframework；落点 Resources/stdlib+Tests test-fixtures；run=swift test）
- [x] 2.3 `xtask_test_android.z42` AndroidBackend（cargo-ndk+AAR；落点 assets/stdlib+androidTest assets；run=gradle connectedAndroidTest）

## 阶段 3: CLI 接线
- [x] 3.1 `xtask.z42.toml` [sources] 注册 4 新文件
- [x] 3.2 `xtask_cli.z42` `_testRouter` 加 `platform` 子路由 + `_dispatchTest` 分派

## 阶段 4: 验证 + 文档 + 归档
- [x] 4.1 build xtask 项目无编译错误（含 4 新文件）
- [x] 4.2 wasm 端到端：build → assets → run（local node）与旧 .sh 一致
- [x] 4.3 iOS/Android：编译 + 移植保真度核对（端到端留 CI）
- [x] 4.4 dotnet GoldenTests 绿（未碰编译器）
- [x] 4.5 docs/design/runtime/cross-platform.md 同步（契约 + 实现原理）
- [x] 4.6 归档 + 释放 toolchain 锁 + commit

## 备注
- 旧 platforms/*/{build,test}.sh 保留（CI-proven 后另开 change 删）
- 落点路径以各平台现有 build.sh 实际值为准，2.x 逐一核对
