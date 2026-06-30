# Spec: build/publish 命令归位

## ADDED Requirements

### Requirement: `z42 build` 转发 z42c

#### Scenario: build 经 launcher 编译
- **WHEN** `z42 build <project.z42.toml>`
- **THEN** launcher 起 `z42vm programs/z42c/z42c.driver.zpkg -- build <project.z42.toml>`，继承 stdio + exit code，产出 zpkg

### Requirement: `z42 publish` 转发 z42b

#### Scenario: publish 经 launcher 转发到 z42b
- **WHEN** `z42 publish <toml> --rid <desktop-rid>`
- **THEN** launcher 透传 argv 给 z42b（`programs/z42b/z42.builder.zpkg`），z42b 执行 publish

### Requirement: publish 自带编译（build-if-needed）

#### Scenario: zpkg 已存在 → 直接产 apphost
- **WHEN** z42b publish 且期望 zpkg 已编译
- **THEN** 跳过编译，直接 `Apphost.Produce`（xtask 组装路径，编译环境由调用方控）

#### Scenario: zpkg 不存在 → 先编译再产
- **WHEN** z42b publish 且期望 zpkg 不存在
- **THEN** 先 spawn z42c build `<toml>` 编出 zpkg，再产 apphost（终端用户一步建+部署）

### Requirement: publish 部署布局（bin/payload）随实现迁入 z42b

#### Scenario: bin/payload 行为不变
- **WHEN** `[platform.desktop]` 设 `bin`/`payload`，经 z42b publish
- **THEN** 行为与迁移前 launcher 实现一致：payload zpkg 复制到 `root/payload`、apphost 落 `root/bin`、内嵌相对路径自动算

## MODIFIED Requirements

**Before:** `publish` 实现住 launcher（launcher_export.z42 `_cmdPublishDesktop`）；无 `z42 build`；xtask 须 build 再 publish 两步。
**After:** `build`→z42c、`publish`→z42b（launcher 仅转发）；publish 自带 build-if-needed，build+publish 可一步。现有 `z42 publish desktop` 对外行为不变。

## Pipeline Steps

- [ ] launcher 命令路由（launcher_cli：build 转发集 + publish 转发）
- [ ] z42b publish 实现（builder_publish + builder_cli dispatch + toml deps）
- [ ] z42.project bin/payload 模型（已落地）
