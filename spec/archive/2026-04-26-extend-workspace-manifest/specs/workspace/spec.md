# Spec: Workspace Manifest Schema（C1）

## ADDED Requirements

### Requirement: workspace 根 manifest 文件名固定

#### Scenario: 编译器识别 workspace 根
- **WHEN** 编译器从某目录向上查找 workspace 根
- **THEN** 仅识别**文件名等于 `z42.workspace.toml`** 的文件为 workspace 根
- **AND** 撞到的第一个 `z42.workspace.toml` 即为根，停止向上查找
- **AND** 名为 `<name>.z42.toml` 的文件即使含 `[workspace]` 段也不被识别为根（报 `WS030 InvalidWorkspaceFileName`）

#### Scenario: 找不到 workspace 根
- **WHEN** 一直向上走到文件系统根仍未找到 `z42.workspace.toml`
- **THEN** 当前 manifest（若有）作为独立单工程，不进入 workspace 模式

---

### Requirement: `[workspace]` 段成员声明（glob + exclude + default-members）

#### Scenario: glob 展开为成员目录
- **WHEN** `[workspace] members = ["libs/*", "apps/*"]`
- **THEN** 编译器枚举 `libs/` 和 `apps/` 下的子目录
- **AND** 子目录内必须**恰好一份** `<name>.z42.toml`
- **AND** 否则跳过该子目录（不报错，仅 debug 日志）

#### Scenario: 显式数组与 glob 混用
- **WHEN** `members = ["libs/*", "apps/main"]`
- **THEN** 编译器同时支持目录 glob 和具体路径
- **AND** 重复路径自动去重

#### Scenario: exclude 优先于 members
- **WHEN** `members = ["libs/*"]` 且 `exclude = ["libs/sandbox-*"]`
- **THEN** `libs/sandbox-foo` 不参与编译，即使它含合法 manifest

#### Scenario: default-members 控制默认编译子集
- **WHEN** workspace 含 `default-members = ["apps/hello"]`
- **AND** User 在 workspace 根运行 `z42c build`（无 `-p`、无 `--workspace`）
- **THEN** 仅编译 `apps/hello`

#### Scenario: default-members 必须是 members 子集
- **WHEN** `default-members = ["nonexistent"]` 但 nonexistent 不在 members 展开结果中
- **THEN** 报 `WS031 InvalidDefaultMembers`，列出未匹配项

#### Scenario: 同一目录两份 manifest
- **WHEN** 某 member 目录下有两份 `*.z42.toml`
- **THEN** 报 `WS005 AmbiguousManifest`，列出冲突文件

#### Scenario: orphan member（在子树内但未被 members 命中）
- **WHEN** 某子目录内有 `<name>.z42.toml`，且未被 `members` 展开命中、也不在 `exclude` 中
- **THEN** 报 `WS007 OrphanMember`（warning 级，不阻塞编译；提示用户加入 members 或 exclude）

---

### Requirement: `[workspace.project]` 共享元数据

#### Scenario: 成员引用共享 version
- **WHEN** workspace 根 `[workspace.project] version = "0.1.0"`
- **AND** member 的 `[project] version.workspace = true`
- **THEN** 该 member 最终生效的 version 为 `"0.1.0"`

#### Scenario: 引用未声明的字段
- **WHEN** member 写 `version.workspace = true`
- **AND** workspace 根 `[workspace.project]` 段不存在或不含 `version` 字段
- **THEN** 报 `WS032 WorkspaceFieldNotFound`，列出 member 路径与缺失字段名

#### Scenario: 支持的共享字段
- **WHEN** `[workspace.project]` 段
- **THEN** 仅以下字段允许共享：`version` / `authors` / `license` / `description`
- **AND** 不允许共享：`name` / `kind` / `entry`（这些 member 必须自己声明）

#### Scenario: 字段类型校验
- **WHEN** `[workspace.project] version = 42`（非字符串）
- **THEN** 报 `WS033 InvalidWorkspaceProjectField`，说明字段类型

---

### Requirement: 依赖语法对齐 Cargo 风格

#### Scenario: 简洁形式 `dep.workspace = true`
- **WHEN** workspace 根 `[workspace.dependencies] my-utils = { path = "libs/my-utils", version = "0.1.0" }`
- **AND** member `[dependencies] "my-utils".workspace = true`
- **THEN** 该 member 实际依赖等同于 workspace 中的声明

#### Scenario: 表形式 `{ workspace = true, ... }`
- **WHEN** member `[dependencies] "my-utils" = { workspace = true, optional = true }`
- **THEN** 解析为引用 workspace + 局部修饰 `optional = true`
- **AND** workspace 中的字段（`path` / `version`）保留，与局部 `optional` 合并

#### Scenario: 引用未声明的依赖
- **WHEN** member 写 `"unknown".workspace = true`
- **AND** workspace 根 `[workspace.dependencies]` 无 `unknown` 项
- **THEN** 报 `WS034 WorkspaceDependencyNotFound`

#### Scenario: 旧语法 `version = "workspace"` 不再接受
- **WHEN** member 写 `[dependencies] "my-utils" = { version = "workspace" }`
- **THEN** 报 `WS035 LegacyWorkspaceVersionSyntax`，提示改用 `.workspace = true`

---

### Requirement: virtual manifest（workspace 根不含 `[project]`）

#### Scenario: 纯协调 manifest
- **WHEN** `z42.workspace.toml` 含 `[workspace]` 但**不含** `[project]`
- **THEN** 该文件为 virtual manifest，不参与编译产出
- **AND** 仅做成员协调和共享配置容器

#### Scenario: 根 manifest 同时含 `[project]`
- **WHEN** `z42.workspace.toml` 同时含 `[workspace]` 和 `[project]`
- **THEN** 报 `WS036 RootManifestMustBeVirtual`
- **AND** 提示用户拆分：将 `[project]` 内容移到独立的 member 目录（如 `apps/<name>/<name>.z42.toml`），workspace 根只保留协调职责

> 决策 D2：根 manifest 不允许兼任 member，强制拆分以保持清晰。

---

### Requirement: 路径字段模板变量

#### Scenario: 内置变量集合
- **WHEN** manifest 路径字段含 `${name}` 形式的占位
- **THEN** 仅以下 4 个变量被识别：
  - `${workspace_dir}` — workspace 根的绝对路径
  - `${member_dir}` — 当前 member 目录的绝对路径
  - `${member_name}` — 当前 member 的 `[project] name`
  - `${profile}` — 当前激活的 profile 名（`debug` / `release` / 自定义）
- **AND** 其他变量名（含 `${env:NAME}`）解析为 `WS037 UnknownTemplateVariable`，错误信息提示"`${env:...}` 仅在未来版本支持"

#### Scenario: 在 include 路径中展开 workspace_dir
- **WHEN** member `include = ["${workspace_dir}/presets/lib-defaults.toml"]`
- **AND** workspace 根路径为 `/repo`
- **THEN** include 实际解析路径为 `/repo/presets/lib-defaults.toml`

#### Scenario: 在 out_dir 中展开 profile
- **WHEN** workspace 根 `[workspace.build] out_dir = "dist/${profile}"`
- **AND** CLI 选定 `--release`
- **THEN** member 最终 out_dir 解析为 `dist/release`

#### Scenario: 字面量 $ 使用 $$ 转义
- **WHEN** 路径字段含 `"$$keep"`
- **THEN** 展开结果为字面量字符串 `$keep`

#### Scenario: 不允许嵌套变量
- **WHEN** 路径字段含 `${a${b}}`
- **THEN** 报 `WS038 InvalidTemplateSyntax`，提示嵌套不允许

#### Scenario: 未闭合的变量
- **WHEN** 路径字段含 `${unfinished`（缺 `}`）
- **THEN** 报 `WS038 InvalidTemplateSyntax`

#### Scenario: 变量在不允许的字段使用
- **WHEN** member `[project] version = "${profile}"`
- **THEN** 报 `WS039 TemplateVariableNotAllowed`，列出字段路径与可允许该字段的字段白名单

#### Scenario: 允许使用变量的字段白名单
- **WHEN** 解析配置
- **THEN** 仅以下字段允许变量替换：
  - `include` 数组各元素
  - `[workspace.build] out_dir / cache_dir`（C1 仅解析占位，C3 实施）
  - `[workspace.dependencies] xxx.path` 与 `[dependencies] xxx.path`
  - `[sources] include / exclude` 中的 glob 模式
- **AND** 其他字段（`name` / `version` / `kind` / `entry` 等标量元数据，以及 `members` glob）出现 `${...}` 一律报 `WS039`

#### Scenario: 展开顺序与缓存
- **WHEN** 同一 manifest 多个字段引用同变量（如 `${profile}`）
- **THEN** 编译器在 manifest 加载时一次性展开所有路径字段
- **AND** 展开结果存入 `ResolvedManifest`，下游不再处理模板

---

### Requirement: Member 不允许写禁用段

#### Scenario: Member 写 `[profile.*]`
- **WHEN** member 的 `<name>.z42.toml` 含 `[profile.debug]` 或 `[profile.release]`
- **THEN** 报 `WS003 ForbiddenSectionInMember` (error 级)
- **AND** 提示 profile 只能在 workspace 根声明

#### Scenario: Member 写 `[workspace]` 或 `[workspace.*]`
- **WHEN** member 的 `<name>.z42.toml` 含 `[workspace]` 或 `[workspace.project]` / `[workspace.dependencies]` 等子段
- **THEN** 报 `WS003 ForbiddenSectionInMember` (error 级)

> 决策 D6：member 写禁用段直接 error，不降级 warning。

---

## MODIFIED Requirements

### Requirement: 工作区根 manifest 文件名

**Before**：[docs/design/project.md](../../../docs/design/project.md) L370 称"monorepo 根目录的 z42.toml"，L429 示例又用 `z42.workspace.toml`，无唯一约定。

**After**：固定为 `z42.workspace.toml`。Member 仍用 `<name>.z42.toml`。

### Requirement: `[workspace] members` 支持的形态

**Before**：仅显式字符串数组（每个元素是具体目录路径）。

**After**：支持 glob 模式 + 显式路径混用，配合 `exclude` 排除子集。

### Requirement: 引用 workspace 共享依赖的语法

**Before**：`[dependencies] "name" = { version = "workspace" }`

**After**：`[dependencies] "name".workspace = true` 或 `[dependencies] "name" = { workspace = true, ... }`。旧语法不再接受（pre-1.0 阶段不留兼容路径）。

### Requirement: 共享元数据机制

**Before**：每个 member 重复声明 `version` / `license` / `authors`。

**After**：在 workspace 根 `[workspace.project]` 段统一声明；member 用 `version.workspace = true` 等引用。

---

## Pipeline Steps

本变更影响的 pipeline 阶段：

- [x] **Manifest 解析层**（新增）：`WorkspaceManifest.cs` + `MemberManifest.cs`，含字段验证与错误码
- [ ] Lexer：不动
- [ ] Parser / AST：不动（z42 源码无变更）
- [ ] TypeChecker：不动
- [ ] IR Codegen：不动
- [ ] zbc 二进制格式：不动
- [ ] VM interp：不动

> C1 是工程文件层面的 schema 演进，**不触及编译器核心 pipeline 与 zbc 格式**。

## IR Mapping

无 IR 变更（C1 不触及 IR）。

---

## 错误码索引（本变更新增/调整）

| 码 | 含义 | 级别 |
|---|---|---|
| WS003 | Member / preset 内出现禁用段（`[workspace.*]` / `[profile.*]`） | error |
| WS005 | 同一目录两份 `*.z42.toml` 引发歧义 | error |
| WS007 | Manifest 在 workspace 子树内但未被 members 命中 | warning |
| WS030 | `[workspace]` 段出现在非 `z42.workspace.toml` 文件 | error |
| WS031 | `default-members` 含未匹配项 | error |
| WS032 | Member 引用 workspace 共享字段，但根未声明 | error |
| WS033 | `[workspace.project]` 字段类型错误 | error |
| WS034 | Member 引用未声明的 workspace 依赖 | error |
| WS035 | 出现已废弃的 `version = "workspace"` 语法 | error |
| WS036 | Workspace 根 manifest 同时含 `[workspace]` 与 `[project]` | error |
| WS037 | 路径模板含未知变量（含 `${env:...}` 暂不支持） | error |
| WS038 | 路径模板语法非法（嵌套 / 未闭合 / 空名） | error |
| WS039 | 模板变量出现在不允许的字段（如 `version` / `members`） | error |

> 错误码序列与 C2/C3/C4 各自占用独立段（WS020+ / WS010+ / WS001+），互不冲突。完整索引在 C4 归档时整理到 [docs/design/error-codes.md](../../../docs/design/error-codes.md)。
