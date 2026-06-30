# Proposal: build/publish 命令归位（build→z42c、publish→z42b、publish 自带编译）

## Why

当前 toolchain 命令面分布不一致：`test`/`bench`/`clean` 由 launcher 转发给 z42b，但
`publish` 的实现却**长在 launcher 里**（launcher_export.z42 的 `_cmdPublishDesktop`），且
**没有 `build` 命令**。同时 xtask 组装发行包要「先 `build` 编译 zpkg，再 `publish` 产 apphost」
两步走。

理顺为：**编译=z42c、部署编排=z42b、launcher 只做命令面转发**；并让 `publish` 在 zpkg 未编译
时**自带编译**，把 build+publish 两步并成一步——这是 add-package-layout-config 用 `z42 publish`
驱动 xtask 组装的前置。

## What Changes

1. **launcher 加 `build` → 转发 z42c**：照搬现有 `_forwardZ42b`，新增 `_forwardZ42c`，起
   `z42vm programs/z42c/z42c.driver.zpkg -- build …`。`z42 build` = 编译，归 z42c。
2. **publish 迁 z42b**：把 `_cmdPublishDesktop`（含 add-package-layout-config 的 bin/payload 消费）
   + desktop-only helper 从 launcher_export 搬进 z42b 的新 `builder_publish.z42`；z42b router 已注册的
   `publish`（builder_cli.z42:51）dispatch 接到它。launcher 的 `publish` 改为 `_forwardZ42b` 转发
   （与 test/bench 一致）。export(ios/android/wasm) **留在 launcher 不动**。
3. **publish 自带编译（build-if-needed）**：z42b publish 解析期望 zpkg，**不存在则先 spawn z42c
   build `<toml>`，再产 apphost**。xtask 两步并一步。

> publish 不依赖 z42.project / z42.build（只用 `z42.workload.desktop` 的 `Apphost.Produce` +
> z42.toml + z42.io），**不触发** z42b 的 z42.project 命名空间自举串味雷区——这是迁移可行的关键。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/launcher/core/launcher_cli.z42` | MODIFY | `build` 加入转发集（→z42c）；`publish` 改 `_forwardZ42b` 转发；移除 Resolve 中 `publish` 叶子分发；新增 `_forwardZ42c` |
| `src/toolchain/launcher/core/launcher_export.z42` | MODIFY | 移除 `_cmdPublish`/`_cmdPublishDesktop` + desktop-only publish helper（搬去 z42b）；保留 export(ios/android/wasm) + 其仍用的共享 helper |
| `src/toolchain/builder/core/builder_publish.z42` | NEW | publish 实现：bin/payload 布局 + 内联 patcher 产 apphost + build-if-needed（spawn z42c） |
| `src/toolchain/builder/core/builder_apphost.z42` | NEW | 内联 apphost patcher（`_pubProduceApphost`）——z42b 须纯 stdlib 依赖（测试运行器上下文），不能 dep z42.workload.desktop（Decision 6） |
| `src/toolchain/builder/core/builder_cli.z42` | MODIFY | `publish` dispatch 从 "pending" 接到 `builder_publish`（new/build/export 仍 pending）+ publish ArgParser 加 --rid/--output |
| `src/toolchain/builder/core/z42.builder.z42.toml` | MODIFY | `include` 加 builder_publish.z42 + builder_apphost.z42；deps **只加 `z42.toml`**（stdlib-only；**不**加 z42.workload.desktop——Decision 6） |
| `src/libraries/z42.project/src/DesktopConfig.z42` | MODIFY | `bin`/`payload` 字段（已落地于 add-package-layout-config 第①步，归属本 change） |
| `src/libraries/z42.project/src/ManifestLoader.z42` | MODIFY | 解析 `bin`/`payload`（同上，归属本 change） |
| `docs/design/compiler/project.md` | MODIFY | `[platform.desktop] bin/payload` 用户面说明（同上，归属本 change） |
| `docs/design/toolchain/launcher-command-dispatch.md` | MODIFY | 命令路由更新：build→z42c、publish→z42b、publish build-if-needed |
| `src/toolchain/builder/README.md` | MODIFY | publish 不再 PARKED；记 builder_publish + build-if-needed |
| `src/toolchain/builder/core/tests/publish-layout/` | NEW | publish bin/payload 布局 + build-if-needed 的单元/e2e |

**只读引用**：launcher 的 `_forwardZ42b` / `_desktopApphostStub` / `Apphost`(z42.workload.desktop) / z42b builder_cli 现状。

## Out of Scope

- `build` 的 in-process ICompiler API（wire-z42b-host-build 的原愿景）→ 本 change 用 z42c 子进程转发取代该需求，更轻。
- z42b 的 `new` / `export` 编排（仍 PARKED）。
- xtask 改用 `z42 publish` 组装发行包 → 归 add-package-layout-config（本 change 归档后回去做）。
- publish 的 ios/android/wasm（仍 B5 未实现）。

## Open Questions

- [ ] build-if-needed 在 **xtask 组装语境**下，z42b 自动 build 用的 z42c/Z42_LIBS 是否与 xtask 期望一致（字节一致）？倾向：xtask 仍显式控编译环境，build-if-needed 主要服务终端用户 `z42 publish myapp`。design.md Decision 3。
