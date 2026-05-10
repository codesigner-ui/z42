# Proposal: workspace 编译核心运行时（C4a）

## Why

C1+C2+C3 完成后，workspace manifest schema / include / policy / 集中产物全部就绪，但 `z42c build` 仍是单工程模式（仅 ProjectManifest 路径）。C4a 让 z42c **支持 workspace 模式编译**：从任意子目录发起编译时自动找到 workspace 根、按依赖拓扑串行编译每个 member。

并行编译 / 跨 member 增量复用 / RunCommand 等增强放在 future（C4 拆分中明确收紧到核心运行时）。

## What Changes

| 变更 | 说明 |
|---|---|
| **workspace 根发现集成** | z42c 在解析 path 参数前先调用 ManifestLoader.DiscoverWorkspaceRoot；发现到 → workspace 模式；否则 → 现有单工程 / 单文件模式 |
| **MemberDependencyGraph** | DFS 三色检测环（WS006），输出拓扑层 |
| **WorkspaceBuildOrchestrator** | 串行编译每个 member（C4a 不做并行）；上游失败 → 下游 blocked |
| **BuildCommand 入口** | 解析 `-p` / `--workspace` / `--exclude` / `--release` / `--profile`；workspace 模式调用 orchestrator |
| **PackageCompiler 接受 ResolvedManifest** | 新增 `CompileFromResolved(ResolvedManifest)` 入口；保留现有 `Compile(ProjectManifest)` 不动 |
| **WS001 / WS002 / WS006 启用** | 重复 member name / 排除冲突 / 循环依赖 |
| **examples/workspace-full/** | 跨 member 依赖示例（core ← utils ← hello）|

## Scope（允许改动的文件）

### 新增（NEW）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Driver/Commands/CommandContext.cs` | CommandContext + IZ42Command 接口（含 workspace 根 / profile / verbosity） |
| `src/compiler/z42.Driver/Commands/BuildCommand.cs` | build / check 命令分派（workspace + 单工程 + 单文件三模式） |
| `src/compiler/z42.Compiler/MemberDependencyGraph.cs` | 跨 member 依赖图 + DFS 三色环检测 |
| `src/compiler/z42.Compiler/WorkspaceBuildOrchestrator.cs` | 串行编译调度 |
| `src/compiler/z42.Tests/MemberDependencyGraphTests.cs` | 拓扑 / 环 / WS006 |
| `src/compiler/z42.Tests/WorkspaceBuildOrchestratorTests.cs` | 串行编译 / blocked 传播 / WS001 / WS002 |
| `src/compiler/z42.Tests/BuildCommandTests.cs` | 入口路由 / 模式判断 |
| `examples/workspace-full/z42.workspace.toml` | 跨 member 依赖示例根 |
| `examples/workspace-full/libs/core/core.z42.toml` | 基础 lib（无依赖） |
| `examples/workspace-full/libs/utils/utils.z42.toml` | 中间 lib（依赖 core） |
| `examples/workspace-full/apps/hello/hello.z42.toml` | exe（依赖 utils） |
| `examples/workspace-full/libs/core/src/Core.z42` | 样例源 |
| `examples/workspace-full/libs/utils/src/Utils.z42` | 样例源 |
| `examples/workspace-full/apps/hello/src/Main.z42` | 样例源 |

### 修改（MODIFY）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Driver/Program.cs` | 在 path 解析前接 workspace 发现；workspace 模式分派给 BuildCommand 路由 |
| `src/compiler/z42.Pipeline/PackageCompiler.cs` | 新增 `CompileFromResolved(ResolvedManifest, ...)` 入口供 orchestrator 调用 |
| `src/compiler/z42.Project/ManifestErrors.cs` | 追加 WS001 / WS002 / WS006 工厂方法 |
| `docs/design/project.md` | L7 "z42c 工作目录无关性 + 命令矩阵（基础）" 章节 |
| `docs/design/compiler-architecture.md` | WorkspaceBuildOrchestrator + MemberDependencyGraph 设计 |
| `docs/design/error-codes.md` | WS001 / WS002 / WS006 |
| `docs/dev.md` | 简短 workspace 命令示例 |

### 只读引用

| 文件路径 | 用途 |
|---------|------|
| `src/compiler/z42.Project/ManifestLoader.cs` | 调用 LoadWorkspace |
| `src/compiler/z42.Project/CentralizedBuildLayout.cs` | 产物路径来源（已 C3 实施） |
| `src/compiler/z42.Project/ResolvedManifest.cs` | EffectiveProductPath 消费 |

## Out of Scope（明确推迟）

- **并行编译**（`Task.WhenAll` + `--jobs N`）→ future
- **三层增量判定**（manifest_hash / upstream_zpkg_hash）→ future；C4a 只复用现有 source_hash
- **fail-fast / blocked 标记**：C4a 串行实现，第一个失败即终止
- **RunCommand**（编译 + 启动 VM）→ 单独 spec 或 C4d
- **TestCommand** → M7 阶段
- **InfoCommand / MetadataCommand / TreeCommand / LintManifestCommand** → C4b
- **CleanCommand / NewCommand / FmtCommand** → C4c

## Open Questions

无。

## 决策记录

| # | 决策 | 选择 |
|---|---|---|
| D4a.1 | 并行 vs 串行 | C4a 串行（async 调用但 await 一个个）；并行留 future |
| D4a.2 | 增量复用机制 | 复用现有 source_hash；跨 member 增量留 future |
| D4a.3 | check 命令实施方式 | 复用 BuildCommand，加 `--check-only` 标志（不写产物） |
| D4a.4 | workspace 模式 fallback | 显式 `--no-workspace` 参数强制单工程 |
