# Proposal: 扩展 z42.toml workspace manifest schema（C1）

## Why

[docs/design/project.md](../../../docs/design/project.md) 的 L6 段当前 workspace 模型有以下问题：

1. **工作区根文件名不一致**：第 370 行说"monorepo 根目录的 z42.toml"，第 429 行示例又写 `z42.workspace.toml`，无唯一约定，编译器无法据此识别根。
2. **`members` 仅支持显式数组**，新增 member 必须修改根 manifest，与 Cargo 等成熟工具差距大。
3. **缺共享元数据机制**：`version` / `license` / `authors` 必须在每个 member 重复声明，无法集中治理。
4. **依赖语法语义混乱**：当前 `version = "workspace"` 把"引用 workspace 共享值"塞进 `version` 字段的值层面，与 Cargo 的 key 层面 `xxx.workspace = true` 不一致，扩展性差（无法表达"引用 workspace 共享但加局部 features"）。
5. **缺 `exclude` / `default-members`**：无法在 monorepo 中排除沙盒目录或为 `z42c build` 设默认子集。

不解决这些问题，后续 C2（include 机制）/ C3（policy + 集中产物）/ C4（编译工具命令矩阵）都无法稳定实施，因为它们都依赖 schema 的最终形态。

## What Changes

| 变更 | 说明 |
|------|------|
| **Workspace 根文件名固定为 `z42.workspace.toml`** | 编译器据此识别 workspace 根；与 member 的 `<name>.z42.toml` 区分 |
| **`[workspace] members` 支持 glob** | `members = ["libs/*", "apps/*"]` |
| **`[workspace] exclude`** | 从 glob 结果中排除目录 |
| **`[workspace] default-members`** | `z42c build` 不带 `-p` 时默认编译的子集 |
| **新增 `[workspace.project]` 段** | 共享元数据（`version` / `authors` / `license` / `description`）；成员用 `xxx.workspace = true` 引用 |
| **依赖语法对齐 Cargo 风格** | `[dependencies]` 中 `dep.workspace = true` 替代旧的 `version = "workspace"` |
| **明确 virtual manifest 概念** | workspace 根可不含 `[project]` 段，纯做协调 |
| **路径字段支持模板变量** | `${workspace_dir}` / `${member_dir}` / `${member_name}` / `${profile}` 四个内置只读变量；仅在白名单路径字段（`include`、`out_dir`、`cache_dir`、`dependencies.path`、`sources.include/exclude`）允许；不允许用户自定义；语法 `${name}`，字面量用 `$$` |
| **现行 L6 文档全面重写** | [docs/design/project.md](../../../docs/design/project.md) L6 段同步修订 |

## Scope（允许改动的文件）

按 [.claude/rules/workflow.md](../../../.claude/rules/workflow.md) 阶段 3 要求，列出每个具体文件路径与变更类型。

### 新增（NEW）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Project/WorkspaceManifest.cs` | `z42.workspace.toml` 数据模型（含 [workspace] / [workspace.project] / [workspace.dependencies]，C1 仅解析 [workspace.build] / [policy] 占位） |
| `src/compiler/z42.Project/MemberManifest.cs` | member `<name>.z42.toml` 数据模型，支持 `xxx.workspace = true` 引用语义 |
| `src/compiler/z42.Project/ResolvedManifest.cs` | 合并后最终配置 + `FieldOrigin` 来源链 |
| `src/compiler/z42.Project/ManifestErrors.cs` | WS003 / WS005 / WS007 / WS030–WS039 错误码常量 + `ManifestException` 异常类型 |
| `src/compiler/z42.Project/ManifestLoader.cs` | 入口：发现 → 解析 → 合并 → 校验 |
| `src/compiler/z42.Project/GlobExpander.cs` | members glob 展开（`*` / `**` 目录级） |
| `src/compiler/z42.Project/PathTemplateExpander.cs` | 4 个内置变量展开 + `$$` 转义 + WS037/038 诊断 |
| `src/compiler/z42.Tests/WorkspaceDiscoveryTests.cs` | 向上查找根 / WS030 / 找不到根 |
| `src/compiler/z42.Tests/MembersExpansionTests.cs` | glob 展开 / exclude / default-members / WS005 / WS007 / WS031 |
| `src/compiler/z42.Tests/WorkspaceProjectInheritanceTests.cs` | `version.workspace = true` / WS032 / WS033 / 不可共享字段 |
| `src/compiler/z42.Tests/WorkspaceDependencyInheritanceTests.cs` | `dep.workspace = true` / 表形式 / WS034 / WS035 |
| `src/compiler/z42.Tests/MemberForbiddenSectionsTests.cs` | WS003（profile / workspace 段在 member） |
| `src/compiler/z42.Tests/VirtualManifestTests.cs` | WS036（根含 `[project]`） |
| `src/compiler/z42.Tests/PathTemplateExpanderTests.cs` | 4 变量展开 / `$$` / WS037/038/039 / 字段白名单 |
| `examples/workspace-basic/z42.workspace.toml` | 样例 workspace 根（用于端到端解析验证） |
| `examples/workspace-basic/libs/greeter/greeter.z42.toml` | 样例 lib member |
| `examples/workspace-basic/libs/greeter/src/Greeter.z42` | 样例源（最小可解析） |
| `examples/workspace-basic/apps/hello/hello.z42.toml` | 样例 exe member |
| `examples/workspace-basic/apps/hello/src/Main.z42` | 样例源 |
| `examples/workspace-basic/expected_resolved.json` | 解析结果 golden |

### 修改（MODIFY）

| 文件路径 | 说明 |
|---------|------|
| `docs/design/project.md` | L6 段全面重写 + 末尾"完整字段速查"区追加新字段 + 路径模板章节 |
| `docs/design/compiler-architecture.md` | 新增 ManifestLoader 模块说明（CLAUDE.md "实现原理文档规则"） |
| `docs/design/error-codes.md` | 追加 WS003/005/007/030-039 索引（如不存在则同步创建） |
| `docs/roadmap.md` | 添加一行"工程文件 schema 演进 — workspace L6 增强（C1）" |
| `src/compiler/z42.Project/z42.Project.csproj` | 若有依赖项变化（如新加 `Microsoft.Extensions.FileSystemGlobbing`）则修改；如不需要可不动 |

### 删除（DELETE）

无。

### 只读引用（不修改）

| 文件路径 | 用途 |
|---------|------|
| `src/compiler/z42.Project/ZpkgReader.cs` | 理解现行 manifest 解析路径（C1 不动） |
| `src/compiler/z42.Project/ZpkgWriter.cs` | 理解 zpkg 写出（C1 不动） |
| `src/compiler/z42.Compiler/PackageCompiler.cs` | 理解编译器入口在何处汇合 manifest（如存在） |
| `src/compiler/z42.Driver/Program.cs` | 理解 z42c CLI 入口（C1 不改 CLI 行为） |

## Out of Scope

- **`include` 机制** → C2
- **`[policy]` 段 + `[workspace.build]` 集中产物** → C3
- **z42c 命令矩阵（`info --resolved` / `metadata` / `tree` 等）** → C4
- **lockfile / registry / publish** → future
- **lint 实际生效**（`[workspace.lints]` 仅占位语法）→ future
- **现有 [src/runtime/Cargo.toml](../../../src/runtime/Cargo.toml) 拆分**：z42 编译器/VM 自身仍保持单 crate，不在本变更范围

## Open Questions

无。§12 决策（D1–D7）已由 User 确认（2026-04-26 对话），其中 `[workspace.package]` 改为 `[workspace.project]` 与 `[project]` 段名保持一致。

## 决策记录摘要

| # | 决策 | 选择 |
|---|------|-----|
| D1 | Workspace 根文件名 | `z42.workspace.toml` |
| D2 | 根 manifest 是否兼任 member | 必须拆两份文件 |
| D3 | 数组合并语义（C2 用） | 整体覆盖 |
| D4 | `[workspace.dependencies]` 是否拆独立文件 | 默认内嵌，不拆 |
| D5 | Policy 锁定字段集合（C3 用） | 默认只锁产物路径 |
| D6 | Member 写 `[profile.*]` 是 error 还是 warning | error |
| D7 | include 数组是否允许 glob（C2 用） | 不允许 |
| D8 | 是否在 C1 引入路径模板变量 | 是；4 个内置只读（`workspace_dir` / `member_dir` / `member_name` / `profile`）；语法 `${name}`；`${env:NAME}` 留 future |

D3/D5/D7 留给 C2/C3 落地；D1/D2/D4/D6/D8 在本变更生效。
