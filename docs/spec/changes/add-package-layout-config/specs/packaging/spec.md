# Spec: 配置驱动的发行包布局

## ADDED Requirements

### Requirement: 组件声明 apphost 部署布局

#### Scenario: 默认约定布局
- **WHEN** 组件 `z42.toml` 含 `[platform.desktop] apphost = true` 且未写 `bin`/`payload`
- **THEN** publish 把 apphost 二进制放到 `bin/<name>`、payload zpkg 放到 `programs/<name>/`，apphost 内嵌 payload 路径 = `../programs/<name>/<name>.zpkg`

#### Scenario: 显式覆盖布局（根 launcher）
- **WHEN** 组件声明 `bin = "z42"` + `payload = "programs/launcher/"`
- **THEN** apphost 二进制落部署根 `z42`、payload 落 `programs/launcher/`，内嵌路径 = `programs/launcher/launcher.zpkg`

#### Scenario: bin/payload 相对路径自动推导
- **WHEN** `bin` 与 `payload` 处于不同深度
- **THEN** publish 计算从 `bin` 所在目录到 `payload` 的相对路径写入 apphost，用户无需手算

### Requirement: publish 输出自洽布局子树

#### Scenario: 单组件 publish 到暂存目录
- **WHEN** `z42 publish <comp.toml> --output <staging>/<comp>`
- **THEN** `<staging>/<comp>/` 内同时含 apphost 二进制（在 `bin`/根）+ payload zpkg（在 `payload` 目录），是一个可直接部署的自洽子树

### Requirement: packages.toml 声明发行包组装

#### Scenario: 按 include 选取组件合并
- **WHEN** `packages.toml` 的 `[package.sdk] include = ["z42vm","native","stdlib","z42c-seed","launcher","z42c","z42b"]`
- **THEN** xtask 打包把这些名字解析到各自暂存子树/staging 产物，合并拷入包根，emit manifest

#### Scenario: 加 apphost 仅改配置
- **WHEN** 给 sdk 增加 z42d
- **THEN** 仅需 z42d 组件 toml 有 `[platform.desktop] apphost=true` + `packages.toml` 的 `sdk.include` 追加 `"z42d"`；**xtask 打包代码零改动**

#### Scenario: sdk 与 runtime 的 stdlib 字节一致
- **WHEN** sdk 与 runtime 的 `include` 都含 `"stdlib"`
- **THEN** 两包的 `libs/*.zpkg` 来自同一暂存源、逐字节一致（无 reuse-from-sdk 特例逻辑）

## MODIFIED Requirements

**Before:** 各发行包内容由 `_packageDesktop` / `_buildRuntimePackage` 等函数手写硬编码；加 apphost 要在函数里加「编译 zpkg + 建 programs/ + 造 apphost」三步。
**After:** 包内容由 `packages.toml` 的 `include` 声明 + 组件 toml 的部署布局声明驱动；加 apphost = 配置变更，打包代码不变。产物相对改造前逐字节一致。

## Pipeline Steps

不涉及编译器 pipeline（lexer/parser/...）。受影响：
- [ ] z42.project 清单模型（DesktopConfig / ManifestLoader：`bin`/`payload`）
- [ ] launcher publish（`_cmdPublishDesktop`：输出布局子树）
- [ ] xtask packaging（packages.toml 解析 + include 合并）
