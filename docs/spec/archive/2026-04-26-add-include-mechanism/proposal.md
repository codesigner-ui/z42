# Proposal: 引入 z42.toml include 机制（C2）

## Why

C1 落地后，workspace 根 `[workspace.project]` / `[workspace.dependencies]` 提供了**全仓共享**，但**子树/分组共享**仍无表达力：

- `libs/*` 都用 `kind = "lib"` + 排除测试文件 → 每个 member 重复声明
- `apps/*` 都用统一的 `[sources] include` glob 模式 → 重复
- 一组严格 lints / 一组性能调优 → 无法成组复用

C# Directory.Build.props 用"递归向上发现"解决，但与 z42 显式哲学冲突，且 member 移动时配置突变。本变更引入**显式 include**：member 主动拉入预设片段，类似 C `#include`，位置自由、移动 member 不影响行为。

## What Changes

| 变更 | 说明 |
|---|---|
| **`include` 字段** | 字符串数组，路径相对于声明文件；可在 member 与 preset 中使用 |
| **Preset 文件类型** | 任意 `.toml` 文件，不强制命名约定；含 `[project]`（除身份字段）/ `[sources]` / `[build]` / `[dependencies]`；不允许 `[workspace.*]` / `[policy]` / `[profile.*]` |
| **合并语义** | 标量后者覆盖前者；表字段级合并；数组**整体覆盖**（不连接） |
| **配置生效顺序** | workspace 共享继承 → include 链按声明顺序展开 → member 自身字段覆盖 |
| **路径解析** | 相对于声明 include 的文件；不允许绝对系统路径、URL、glob |
| **循环检测** | DFS 检测环 → WS020 |
| **嵌套深度限制** | 8 层 → WS022 |
| **重复 include 去重** | 同一物理文件被多次拉入不报错（菱形 include 合法） |
| **错误码 WS020-024** | 见 spec |

## Scope（允许改动的文件）

### 新增（NEW）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Project/IncludeResolver.cs` | 解析 include 路径、构造 DAG、检测环 / 深度 / 路径合法性 |
| `src/compiler/z42.Project/ManifestMerger.cs` | 合并语义实现（标量覆盖 / 表合并 / 数组整体覆盖） |
| `src/compiler/z42.Tests/IncludeResolverTests.cs` | 路径解析 / 循环 / 深度 / 重复 / 路径不存在 / WS020-024 |
| `src/compiler/z42.Tests/ManifestMergerTests.cs` | 三种合并语义 / preset 段限制 WS021 |
| `src/compiler/z42.Tests/IncludeIntegrationTests.cs` | 端到端：member include preset → ResolvedManifest 期望值 |
| `examples/workspace-with-presets/z42.workspace.toml` | 含 preset 的样例 workspace |
| `examples/workspace-with-presets/presets/lib-defaults.toml` | 共享 lib 默认值 |
| `examples/workspace-with-presets/presets/strict-lints.toml` | 共享严格规则（C2 仅占位语法） |
| `examples/workspace-with-presets/libs/foo/foo.z42.toml` | 引用 lib-defaults |
| `examples/workspace-with-presets/libs/bar/bar.z42.toml` | 引用 lib-defaults + strict-lints |
| `examples/workspace-with-presets/libs/foo/src/Foo.z42` | 样例源 |
| `examples/workspace-with-presets/libs/bar/src/Bar.z42` | 样例源 |
| `examples/workspace-with-presets/expected_resolved.json` | 解析结果 golden |

### 修改（MODIFY）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Project/ManifestLoader.cs` | 在 workspace 共享继承之后、member 字段合并之前接入 IncludeResolver + ManifestMerger |
| `src/compiler/z42.Project/ResolvedManifest.cs` | `OriginKind` 增加 `IncludePreset` 类型，`FieldOrigin` 携带 preset 文件路径 |
| `src/compiler/z42.Project/ManifestErrors.cs` | 追加 WS020-024 错误码 |
| `src/compiler/z42.Project/MemberManifest.cs` | 增加 `Include` 字段 |
| `src/compiler/z42.Project/WorkspaceManifest.cs` | 不变（workspace 根不允许 include —— 简化语义；future 可放开） |
| `docs/design/project.md` | 新增 include 机制章节；配置生效顺序图 |
| `docs/design/compiler-architecture.md` | ManifestLoader 流程图增加 include 解析阶段 |
| `docs/design/error-codes.md` | 追加 WS020-024 |

### 删除（DELETE）

无。

### 只读引用

| 文件路径 | 用途 |
|---------|------|
| `src/compiler/z42.Project/PathTemplateExpander.cs` | 理解 include 路径中 `${workspace_dir}` / `${member_dir}` 展开（C1 已实施） |

## Out of Scope

- **Preset 命名约定强制**：preset 可放任意位置、任意命名；不强制 `presets/` 目录 → future（社区约定即可）
- **Workspace 根本身的 include**：本变更不允许 `z42.workspace.toml` 写 `include`，避免根级配置溯源复杂；future 评估
- **Glob include**：D7 已决拒绝
- **远端 include / URL**：违反供应链安全，永不引入

## Open Questions

无。设计决策见 design.md。

## 决策记录摘要

| # | 决策 | 选择 |
|---|------|-----|
| D2.1 | include 路径是否允许 glob | ❌（D7 已锁） |
| D2.2 | 数组合并语义 | 整体覆盖（D3 已锁） |
| D2.3 | 嵌套深度上限 | 8 层 |
| D2.4 | include 在合并顺序中的位置 | workspace 共享之后、member 自身字段之前 |
| D2.5 | preset 文件命名约定 | 不强制；任何 `.toml` 均可被 include |
| D2.6 | workspace 根是否可写 include | C2 不允许；future 再评估 |
| D2.7 | 重复 include（菱形）处理 | 去重，不报错 |
