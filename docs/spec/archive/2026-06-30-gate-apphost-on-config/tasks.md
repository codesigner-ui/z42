# Tasks: apphost 改为 [platform.desktop] apphost=true 显式 gate

> 状态：🟢 已完成 | 完成：2026-06-30 | 创建：2026-06-30
> 类型：refactor/fix（publish 语义调整 + 新 manifest 字段）

**变更说明：** `z42 publish desktop` 从「有 publish_dir 就产 apphost」改为「`[platform.desktop] apphost = true` 才产」；publish_dir 降为纯输出位置（缺省 = 项目目录）。
**原因：** 旧逻辑把「输出目录」与「是否产 apphost」耦合在 publish_dir 上；User 要求职责分离——apphost=true 做 gate，publish_dir 只管位置。
**子系统：** stdlib（z42.project）+ toolchain（launcher）+ compiler（z42c.driver toml，⚠️ 被 extract-compile-pipeline-api 持锁——仅 3 行 additive config、与 pipeline 改动正交）。
**文档影响：** docs/design/compiler/project.md（[platform.desktop] 字段表 + publish gating）。

## 进度
- [x] 1.1 DesktopConfig 加 `Apphost` bool 字段（src/libraries/z42.project/src/DesktopConfig.z42）
- [x] 1.2 ManifestLoader._parseDesktop 解析 `apphost`（同上 +1 构造点）
- [x] 1.3 launcher_export.z42 加 `_platformBool` + `_cmdPublishDesktop` gate 改 apphost==true；publish_dir 缺省项目目录
- [x] 1.4 4 个 toml 加 `[platform.desktop] apphost = true`：xtask / launcher / z42b / z42c.driver
- [x] 1.5 docs/design/compiler/project.md 同步字段语义
- [x] V.1 build stdlib GREEN（z42.project 编译，构造点改动）
- [x] V.2 launcher 工程编译 GREEN（launcher_export 改动）
- [x] V.3 行为 smoke：publish desktop 在 apphost=true 产、在无配置报错

## 决策
- publish_dir 缺省 = 项目目录 `"."`（仅位置，非 gate）。
- DesktopConfig 加字段（canonical model）；publish 命令直接读 toml（与既有 publish_dir 读法一致）。两处都更新保持语义一致。
- z42c.driver.toml 在 locked compiler 子系统 → additive config，已标注（与 pipeline 工作正交）。

## 备注
- 注意并行：z42.project 有 untracked `z42.project.z42.toml`（并行改动）；surgical 只提交本变更 hunk。
