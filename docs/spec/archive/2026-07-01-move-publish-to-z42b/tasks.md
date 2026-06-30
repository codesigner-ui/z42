# Tasks: build/publish 命令归位

> 状态：🟢 已完成 | 创建：2026-07-01 | 完成：2026-07-01 | 子系统：toolchain（+ stdlib z42.project）
> 前置于 add-package-layout-config（后者解锁，回去用机制）
>
> **验证报告（GREEN，逐 stage 实跑）**：build-stdlib 22/22 · VM goldens 192/0 · cross-zpkg 2/0 ·
> **stdlib [Test] 272/272（22 库——构建 z42b 反射运行器，纯 stdlib 上下文，E0401 修复确认）** ·
> test compiler 自举 7/7 gen1==gen2 · e2e（build→z42c / publish→z42b / bin-payload / build-if-needed
> 全过）· SDK package 组装 + SHA-256 invariants OK（零回归）。
> 实施中发现并修复 z42b 纯 stdlib 依赖坑（Decision 6 内联 patcher）；记 memory reference_z42b_stdlib_only_deps。

## 进度概览
- [x] 阶段 1: z42b publish 实现（搬迁 + dispatch + deps）— z42b 编译通过
- [x] 阶段 2: launcher build→z42c 转发 + publish→z42b 转发 — launcher 编译通过
- [x] 阶段 3: publish 自带编译（build-if-needed）— e2e 验证自动编译
- [x] 阶段 4: 验证（e2e 全过 + 自举 7/7 + SDK 零回归）+ 文档

## 阶段 0: 已落地（add-package-layout-config 第①步迁来，归属本 change）
- [x] 0.1 `DesktopConfig.z42` `bin`/`payload` 字段
- [x] 0.2 `ManifestLoader.z42` 解析 `bin`/`payload`
- [x] 0.3 `docs/design/compiler/project.md` `[platform.desktop]` bin/payload 用户面文档

## 阶段 1: z42b publish 实现
- [x] 1.1 NEW `builder_publish.z42`：publish 实现（bin/payload 布局 + Apphost.Produce），stub 经 `Z42_APPHOST_TEMPLATE` env 读（非搬 launcher runtime 解析——Decision 3.5）+ 自带最小 toml/path helper
- [x] 1.2 `z42.builder.z42.toml`：`include` 加 builder_publish.z42；deps 加 `z42.workload.desktop` + `z42.toml`（z42.encoding 由 workload.desktop 传递，无需直接 dep）
- [x] 1.3 `builder_cli.z42`：`publish` dispatch 接到 `_cmdPublishZ42b` + publish ArgParser 加 --rid/--output
- [x] 1.4 z42b 编译通过（含新 deps，无 z42.project 串味）

## 阶段 2: launcher 转发
- [x] 2.1 `launcher_cli.z42`：新增 `_forwardZ42c`（spawn bin/z42c apphost）+ `_forwardZ42bEnv`（带额外 env）
- [x] 2.2 `build` 加入早转发集（→z42c）；help 注册 `build`
- [x] 2.3 `publish` 经 `_cmdPublish` 预解析 rid+stub → `_forwardZ42bEnv`（Z42_APPHOST_TEMPLATE）转发 z42b
- [x] 2.4 `launcher_export.z42`：移除 `_cmdPublishDesktop` + 死代码 `_platformBool`；保留 export + run-deploy 共享 helper；launcher 编译通过

## 阶段 3: build-if-needed
- [x] 3.1 `_pubEnsureBuilt`：zpkg 不存在 → `_pubFindZ42c`（Z42_PORTABLE_VM 推 home/bin/z42c）spawn `z42c build` 再产；存在 → 跳过（xtask 路径，字节可控）；z42c 不可定位 → 降级 build-first 提示
- [x] 3.2 e2e：build 后 publish（zpkg 在，跳过编译）✓；裸 publish（zpkg 不在，自动编译 → wrote .zsym）✓

## 阶段 4: 验证 + 文档
- [x] 4.1 现有 publish 行为不变：`xtask package --no-build` 重建（launcher+z42b 新码进包）+ SHA-256 invariants OK + 包组装成功（xtask 仍用自己 _produceApphost，零回归）
- [x] 4.2 e2e 全过 + `xtask test compiler` 自举 7/7 gen1==gen2；完整 GREEN 门后台跑（b62grqj0y）
- [x] 4.3 `launcher-command-dispatch.md`：加「已实施当前路由」段
- [x] 4.4 `builder/README.md`：LIVE/PARKED 重分；builder_publish 入 LIVE
- [x] 4.5 spec scenarios 逐条覆盖（build→z42c / publish→z42b / build-if-needed 双路径 / bin-payload 均 e2e 验证）

## 备注
- xtask 改用 `z42 publish` 组装、bin/payload 应用 → 归 add-package-layout-config（本 change 归档后回去）。届时才有 xtask 内联 patcher → Apphost.Produce 的字节一致验证。
- 本 change 不改 xtask 打包代码，故对发行包字节零影响（xtask 仍走原 producer）。
