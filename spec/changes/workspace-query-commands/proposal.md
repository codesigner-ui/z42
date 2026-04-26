# Proposal: workspace 查询命令 + 错误友好输出（C4b）

## Why

C4a 完成后 workspace **可编译**，但缺**查询能力**：

- 用户：不知道当前 workspace 含哪些 members、哪个是 default、当前 profile 是什么
- 用户：不知道某 member 最终生效配置（Origins 来源链信息没暴露）
- IDE / 第三方工具：需要结构化 JSON 消费 workspace 拓扑
- 用户：错误信息（WSxxx）目前是裸文本，缺上下文展示

C4b 把这些能力作为查询命令落地：`info` / `metadata` / `tree` / `lint-manifest`，并引入 `CliOutputFormatter` 提供 Rust 风格友好错误输出。

## What Changes

| 命令 | 行为 |
|---|---|
| `z42c info` | 打印 workspace 概览（root / members / 当前 profile / default-members） |
| `z42c info --resolved -p <name>` | 输出该 member 最终生效配置 + 每字段来源链（含 🔒 PolicyLocked 标记） |
| `z42c info --include-graph -p <name>` | ASCII 显示 include 链 |
| `z42c metadata --format json` | 输出 workspace 拓扑 JSON（含 `schema_version: 1`），供 IDE 消费 |
| `z42c tree` | ASCII 显示跨 member 依赖图 |
| `z42c lint-manifest` | 仅静态校验：循环 include / orphan / 不存在依赖 / policy 字段错误等 |
| **CliOutputFormatter** | Rust 风格错误输出（`error[WSxxx]: 标题 \n --> file:line \n note: ...`） |

## Scope（允许改动的文件）

### 新增（NEW）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Driver/Commands/InfoCommand.cs` | info / --resolved / --include-graph 实现 |
| `src/compiler/z42.Driver/Commands/MetadataCommand.cs` | --format json 输出 |
| `src/compiler/z42.Driver/Commands/TreeCommand.cs` | ASCII 依赖图 |
| `src/compiler/z42.Driver/Commands/LintManifestCommand.cs` | 静态校验聚合 |
| `src/compiler/z42.Driver/CliOutputFormatter.cs` | 错误友好输出 |
| `src/compiler/z42.Tests/InfoCommandTests.cs` | 输出格式 / Origins 显示 |
| `src/compiler/z42.Tests/MetadataCommandTests.cs` | JSON schema |
| `src/compiler/z42.Tests/TreeCommandTests.cs` | ASCII 输出 |
| `src/compiler/z42.Tests/LintManifestCommandTests.cs` | 校验报告 |
| `src/compiler/z42.Tests/CliOutputFormatterTests.cs` | 错误格式化 |

### 修改（MODIFY）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Driver/Program.cs` | subcommand 路由表新增 info / metadata / tree / lint-manifest |
| `src/compiler/z42.Driver/Commands/BuildCommand.cs` | 错误输出走 CliOutputFormatter |
| `docs/design/project.md` | L7 章节扩展为完整命令矩阵（含查询命令） |
| `docs/design/compiler-architecture.md` | InfoCommand / MetadataCommand 输出 schema |
| `docs/dev.md` | 查询命令使用示例 |

### 只读引用

| 文件路径 | 用途 |
|---------|------|
| `src/compiler/z42.Project/ResolvedManifest.cs` | 消费 Origins / EffectiveProductPath |
| `src/compiler/z42.Compiler/MemberDependencyGraph.cs` | TreeCommand 复用 |

## Out of Scope

- **CleanCommand / NewCommand / FmtCommand** → C4c
- **WS004 完全移除** → C4c
- **`z42c add` / `z42c update` / `z42c publish`** → future
- **完整 metadata schema 演进**（schema_version=2+）→ future

## 决策记录

| # | 决策 | 选择 |
|---|---|---|
| D4b.1 | info 输出格式 | 人类可读字段表（多行 + 缩进 + 颜色未启用），JSON 走 metadata |
| D4b.2 | metadata schema_version | "1"（字符串）；future breaking 时 bump |
| D4b.3 | CliOutputFormatter 范围 | C4b 仅格式化 manifest 错误（WSxxx）；编译错误（Z01xx-Z05xx）保持现有格式 |
| D4b.4 | --no-pretty 标志 | 输出纯文本（CI 友好），默认开启友好格式 |
