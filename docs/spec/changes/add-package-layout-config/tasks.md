# Tasks: 配置驱动的发行包布局

> 状态：🟡 进行中（实施）| 创建：2026-06-30 | 子系统：toolchain（+ stdlib z42.project）

## 实施顺序（User 裁决 2026-06-30）
递进、风险后置：**① 支持 apphost bin/payload 配置（不动 xtask）→ ② 让 xtask 用该配置发布
→ ③ 敲定 packages.toml → ④ 最后重构 xtask 代码**。下面阶段编号沿用，但按此顺序推进。

## 进度概览
- [x] 阶段 1: 部署布局声明（组件 toml `bin`/`payload` + z42.project 模型 + publish 消费）— ① 完成
- [ ] 阶段 2: publish 输出布局子树 — ① 已含（_cmdPublishDesktop 支持 bin/payload，向后兼容）
- [ ] 阶段 3: packages.toml + xtask 组装引擎 — ③
- [ ] 阶段 4: 迁移 desktop SDK + runtime 打包到配置驱动 — ②（让 xtask 用配置）→ ④（重构）
- [ ] 阶段 5: 验证（产物字节一致）+ 文档

## 阶段 1: 部署布局声明（① 完成 2026-06-30）
- [x] 1.1 `DesktopConfig.z42` 增 `bin` / `payload` 可选完整路径字段（空 = 约定默认，由消费方推导）
- [x] 1.2 `ManifestLoader.z42` 解析 `bin` / `payload`
- [ ] 1.3 单元测试：bin/payload 解析 + publish 子树行为（待 ② 接入后随 e2e 覆盖）
- [ ] 1.4 给 launcher/z42c.driver/builder 三个 toml 写显式布局（镜像现状）— 归 ②

## 阶段 2: publish 输出布局子树（① 完成核心 2026-06-30）
- [x] 2.1 `_cmdPublishDesktop` 扩展：`payload` 设则把已编译 zpkg 复制到 `root/payload`、apphost 落 `root/bin`；未设走 legacy 扁平（向后兼容，现有包零回归——package 重建验证）
- [x] 2.2 apphost 内嵌相对路径复用 `Apphost.Produce` 已有的 `_relPath(dir(bin), payload)` 自动计算（无需新写）
- [ ] 2.3 e2e：单组件 publish 产出自洽子树（待 ② / dist 测试覆盖）
- [x] 2.4 用户面文档 `docs/design/compiler/project.md` `[platform.desktop]` 补 bin/payload

## 阶段 3: packages.toml + 组装引擎
- [ ] 3.1 NEW `scripts/package/packages.toml`：sdk + runtime 两个包的 include 列表
- [ ] 3.2 NEW `scripts/package/xtask_packages_config.z42`：解析 packages.toml + include 名解析
- [ ] 3.3 稳定 staging handler：z42vm / native / stdlib / z42c-seed（复用现有 _copyNativeLibs / _pkgCopyLibs / _compilerMembers）
- [ ] 3.4 单元测试：packages.toml 解析 + include 解析

## 阶段 4: 迁移 desktop SDK + runtime
- [ ] 4.1 `_packageDesktop` 改为「按 sdk.include 选组件 → 合并暂存子树 + staging 产物」，删 apphost 三步舞硬编码
- [ ] 4.2 `_buildRuntimePackage` 改为「按 runtime.include 组装」，删 reuse-from-sdk 特例
- [ ] 4.3 打包分发 `xtask_package.z42` 接 packages.toml

## 阶段 5: 验证 + 文档
- [ ] 5.1 sdk 包树逐文件/逐字节 == 改造前（`_pkgSha256Check` + 目录 diff；纯重构不改产物）
- [ ] 5.2 runtime 包同上
- [ ] 5.3 `z42 xtask.zpkg test`（全 stage）全绿
- [ ] 5.4 `z42 xtask.zpkg test dist`（发行包验证）全绿
- [ ] 5.5 NEW `docs/design/toolchain/packaging.md`（实现原理：暂存布局、packages.toml schema、producer 边界、Deferred）
- [ ] 5.6 `docs/design/compiler/project.md` 增 `[platform.desktop] bin`/`payload` 用户面说明
- [ ] 5.7 `src/toolchain/README.md` 指向 packaging.md + packages.toml
- [ ] 5.8 spec scenarios 逐条覆盖确认

## 备注
- Phase 1 不动 mobile（ios/android/wasm）打包 → Deferred packaging-future-mobile。
- z42d/z42i 真正登记进 build/workspace 是独立变更；本变更只让「登记后加 include 即可」成立。
- 硬验收 = 产物字节一致：本质是重构（声明化），不得改变发行包内容。
