# Spec: Policy 与集中产物布局（C3）

## ADDED Requirements

### Requirement: `[policy]` 段语法

#### Scenario: 字段路径表达式
- **WHEN** workspace 根写
  ```toml
  [policy]
  "profile.release.strip" = true
  "build.out_dir"         = "dist"
  ```
- **THEN** 编译器解析为 `{ "profile.release.strip" → true, "build.out_dir" → "dist" }` 的锁定字典
- **AND** 字段路径用点分隔，标识 `[<段>]` + `<字段>` 或嵌套子表

#### Scenario: 字段路径不存在
- **WHEN** policy 写 `"unknown.section.field" = X`，但 manifest schema 中无此字段
- **THEN** 报 `WS011 PolicyFieldPathNotFound`，列出该路径与有效字段路径建议（编辑距离最近的）

#### Scenario: 默认锁定字段
- **WHEN** workspace 根含 `[workspace.build] out_dir = "dist"` 但 `[policy]` 未显式锁定
- **THEN** 编译器**自动**视 `build.out_dir` 与 `build.cache_dir` 为锁定（D5 决策）
- **AND** member 若试图覆盖这两个字段 → WS010

#### Scenario: 显式锁定覆盖默认行为
- **WHEN** workspace 根 `[policy]` 中**显式**列出 `"build.out_dir" = "dist"`
- **THEN** 行为与默认锁定一致；显式声明等价于"开启锁定"
- **AND** 若用户希望放开默认锁定，可通过 `[policy] "build.out_dir" = false`（保留语法但 C3 阶段拒绝，提示 D5 不允许放开默认锁定）

---

### Requirement: Policy 冲突检测（WS010）

#### Scenario: Member 直接覆盖 policy 字段
- **WHEN** workspace 根锁定 `"profile.release.strip" = true`
- **AND** member 写 `[profile.release] strip = false`（已被 C1 WS003 拦截）
- **THEN** WS003 优先报；不进入 policy 检查

#### Scenario: Member 覆盖默认锁定字段
- **WHEN** workspace 根 `[workspace.build] out_dir = "dist"`（默认锁定）
- **AND** member `[build] out_dir = "custom_dist"`
- **THEN** 报 `WS010 PolicyViolation`，错误信息含：
  - 锁定字段名（`build.out_dir`）
  - workspace 锁定值（`"dist"`）
  - member 试图设的值（`"custom_dist"`）
  - workspace 锁定来源文件位置
  - member 字段所在文件位置

#### Scenario: Preset 试图覆盖 policy 字段
- **WHEN** preset 含 `[build] out_dir = "from_preset"`，workspace 锁定 `build.out_dir`
- **THEN** 报 `WS010`，来源标注为 IncludePreset 路径

#### Scenario: 锁定字段与原值一致
- **WHEN** member 写 `[build] out_dir = "dist"`，与 workspace 锁定值相同
- **THEN** 不报错（值相同即合法），但 Origins 显示 PolicyLocked 来源

---

### Requirement: `[workspace.build]` 集中产物布局

#### Scenario: 默认布局
- **WHEN** workspace 根 `[workspace.build]` 未显式声明 `out_dir` / `cache_dir`
- **THEN** 默认值：`out_dir = "dist"`，`cache_dir = ".cache"`
- **AND** 这两个值默认锁定（D5）

#### Scenario: 产物文件命名
- **WHEN** workspace 编译 member `foo`（kind=lib）
- **THEN** 产物路径为 `<workspace_root>/<out_dir>/foo.zpkg`
- **AND** 当 `out_dir = "dist"` 时，最终路径 `<workspace_root>/dist/foo.zpkg`

#### Scenario: Cache 路径按 member 分目录
- **WHEN** workspace 编译 member `foo`，源文件 `src/Foo.z42`
- **THEN** 中间产物路径为 `<workspace_root>/<cache_dir>/foo/src/Foo.zbc`
- **AND** 不同 member 同名源文件不互相覆盖

#### Scenario: Profile 派生路径（通过模板）
- **WHEN** workspace 根 `[workspace.build] out_dir = "dist/${profile}"`
- **AND** CLI 选定 `--release`
- **THEN** 产物路径为 `<workspace_root>/dist/release/foo.zpkg`

#### Scenario: 单工程模式不受集中布局影响
- **WHEN** member 在单工程模式（无 workspace）
- **THEN** `[build] out_dir` 与 `cache_dir` 仍由 member 自己控制，行为不变

---

### Requirement: Member `[build]` 在 workspace 模式

#### Scenario: Workspace 模式下被忽略
- **WHEN** workspace 模式 + member 写 `[build] out_dir = "x"`
- **AND** `build.out_dir` 默认锁定
- **THEN** 报 `WS010 PolicyViolation`（D4 决策：不降级为 warning）
- **AND** 提示 member `[build]` 在 workspace 模式下不可覆盖默认锁定字段

#### Scenario: Member 写 `[build]` 中非锁定字段
- **WHEN** workspace 模式 + member `[build] mode = "interp"`
- **AND** `build.mode` 未锁定
- **THEN** 解析通过，与 member 单工程模式行为一致

#### Scenario: Member `[build]` 完全省略
- **WHEN** member 不含 `[build]` 段
- **THEN** 走 workspace 集中布局；正常解析

---

### Requirement: ResolvedManifest 集中产物字段

#### Scenario: EffectiveOutDir 计算
- **WHEN** workspace 根 `[workspace.build] out_dir = "dist/${profile}"`
- **AND** member name = `foo`，profile = `release`
- **THEN** `ResolvedManifest.Build.EffectiveOutDir` = `<workspace_root>/dist/release`
- **AND** `EffectiveProductPath` = `<workspace_root>/dist/release/foo.zpkg`

#### Scenario: IsCentralized 标记
- **WHEN** ResolvedManifest 来自 workspace 模式
- **THEN** `Build.IsCentralized = true`
- **AND** PackageCompiler 据此选择集中布局而非 member-local

---

## MODIFIED Requirements

### Requirement: WS004 BuildSettingOverridden 行为

**Before**：C1 占位为 warning（"member [build] 与 workspace policy 锁定字段冲突"）。

**After**：升级为 `WS010 PolicyViolation`（error），D4 决策。WS004 保留作为占位编号但实际不再使用（C3 归档时移除）。

> 这是 C1 阶段已预见的演进：C1 占位为 warning 是因为 policy 机制未实施；C3 实施后，已知违规直接 error。

### Requirement: 产物路径计算

**Before**：[ZpkgWriter](src/compiler/z42.Project/ZpkgWriter.cs) 调用方传入 member-local `dist/` 路径。

**After**：调用方先调用 `CentralizedBuildLayout.ResolveProductPath(member, profile)`，传入派生后的绝对路径；`ZpkgWriter` 行为不变。

---

## 错误码索引（C3 新增 / 修订）

| 码 | 含义 | 级别 |
|---|---|---|
| WS010 | Policy 冲突：member / preset 字段值与 root policy 锁定值不一致 | error |
| WS011 | Policy 字段路径不存在 | error |
| WS004 | （废弃）原占位"member [build] 与 policy 冲突"，归并入 WS010 | — |

## Pipeline Steps

- [x] Manifest 解析层（接入 PolicyEnforcer）
- [x] 编译器入口（PackageCompiler）：产物路径走 CentralizedBuildLayout
- [ ] Lexer / Parser / TypeChecker / IR Codegen / VM —— 不动

## IR Mapping

无 IR 变更。
