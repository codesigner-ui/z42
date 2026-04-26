# Proposal: z42c workspace 构建工具链（C4）

## Why

C1+C2+C3 完成后，workspace manifest schema、include、policy、集中产物**全部就绪**，但 z42c CLI 仍是单工程模式：

- `z42c build` 只能编译当前目录的单 manifest
- 无法 `-p <name>` 指定 workspace 成员
- 无 `--workspace` 编译全部
- 无依赖拓扑排序、无并行编译、无跨 member 增量
- 无脚手架（`new --workspace`）、无查询命令（`info` / `metadata` / `tree`）、无清理（`clean`）
- 错误码 WS001-007（成员命名冲突 / 排除冲突 / orphan / 循环依赖等）虽在 manifest 层有占位，但 CLI 层未集成报告

C4 的目标：**把 monorepo 工作流落地为完整 CLI 体验**——一次性配齐构建、检查、运行、查询、清理、脚手架命令；接入拓扑排序与并行编译；从任意子目录都能正确发现 workspace 根。

## What Changes

| 类别 | 命令 / 行为 |
|---|---|
| **构建/检查/运行** | `z42c build` / `--workspace` / `-p` / `--exclude` / `--release` / `--profile` / `--no-incremental` / `--jobs` / `--fail-fast`；`z42c check`；`z42c run -p <name>`；`z42c test`（占位，M7 落地） |
| **查询** | `z42c info`；`z42c info --resolved -p <name>`；`z42c info --include-graph -p <name>`；`z42c metadata --format json`；`z42c tree`；`z42c lint-manifest` |
| **清理** | `z42c clean`；`z42c clean -p <name>` |
| **脚手架** | `z42c new --workspace <dir>`；`z42c new -p <name>`；`z42c init`；`z42c fmt` |
| **发现机制** | 任意子目录运行 → 向上找 `z42.workspace.toml`；无 workspace 时回退单工程模式 |
| **构建调度** | 依赖拓扑排序 → 并行编译 → 增量复用（基于 source_hash） |
| **错误码集成** | WS001-007 在 CLI 入口报告（来自 manifest 层抛出后 CLI 友好化输出） |
| **WS004 清理** | 删除 C3 标记废弃的 WS004 常量与文档引用 |

## Scope（允许改动的文件）

### 新增（NEW）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Driver/Commands/BuildCommand.cs` | 构建/检查/运行命令解析与分派 |
| `src/compiler/z42.Driver/Commands/InfoCommand.cs` | `info` / `info --resolved` / `info --include-graph` |
| `src/compiler/z42.Driver/Commands/MetadataCommand.cs` | `metadata --format json` |
| `src/compiler/z42.Driver/Commands/TreeCommand.cs` | `tree` 显示 member 间依赖图（ASCII） |
| `src/compiler/z42.Driver/Commands/CleanCommand.cs` | `clean` / `clean -p` |
| `src/compiler/z42.Driver/Commands/NewCommand.cs` | `new --workspace` / `new -p` / `init` |
| `src/compiler/z42.Driver/Commands/FmtCommand.cs` | `fmt` 格式化 manifest |
| `src/compiler/z42.Driver/Commands/LintManifestCommand.cs` | `lint-manifest` 静态校验 |
| `src/compiler/z42.Driver/Commands/RunCommand.cs` | `run -p <name>` 编译并执行 |
| `src/compiler/z42.Driver/Commands/CommandContext.cs` | 共享上下文：workspace 根路径、profile 选择、verbosity |
| `src/compiler/z42.Driver/CliOutputFormatter.cs` | 错误码友好输出（WS00x → 人类可读） |
| `src/compiler/z42.Compiler/WorkspaceBuildOrchestrator.cs` | 拓扑排序 + 并行调度 + fail-fast |
| `src/compiler/z42.Compiler/MemberDependencyGraph.cs` | 跨 member 依赖图构造 + 循环检测（WS006） |
| `src/compiler/z42.Compiler/IncrementalReusePolicy.cs` | 跨 member 增量判定（manifest hash 变 → 全量重编） |
| `src/compiler/z42.Tests/CommandRoutingTests.cs` | CLI subcommand 路由与参数解析 |
| `src/compiler/z42.Tests/WorkspaceBuildOrchestratorTests.cs` | 拓扑 / 并行 / fail-fast / 循环检测 WS006 |
| `src/compiler/z42.Tests/MemberDependencyGraphTests.cs` | 依赖图构造 / 循环 |
| `src/compiler/z42.Tests/IncrementalReusePolicyTests.cs` | source_hash 命中 / manifest 变更触发全量 |
| `src/compiler/z42.Tests/InfoCommandTests.cs` | 输出格式 / `--resolved` / `--include-graph` / Origins 显示 |
| `src/compiler/z42.Tests/MetadataCommandTests.cs` | JSON 输出 schema |
| `src/compiler/z42.Tests/TreeCommandTests.cs` | ASCII 输出 |
| `src/compiler/z42.Tests/CleanCommandTests.cs` | 集中清理 / per-member 清理 |
| `src/compiler/z42.Tests/NewCommandTests.cs` | 脚手架生成正确目录结构 |
| `src/compiler/z42.Tests/FmtCommandTests.cs` | 字段排序 / 缩进规范 |
| `src/compiler/z42.Tests/LintManifestCommandTests.cs` | 校验报告 |
| `src/compiler/z42.Tests/RunCommandTests.cs` | 编译并执行 exe member |
| `src/compiler/z42.Tests/WorkspaceBuildIntegrationTests.cs` | 端到端：在 `examples/workspace-full/` 跑完整流程 |
| `examples/workspace-full/z42.workspace.toml` | 综合样例：含 policy + preset + 跨 member 依赖 |
| `examples/workspace-full/presets/lib-defaults.toml` | 共享 lib 预设 |
| `examples/workspace-full/libs/core/core.z42.toml` | 基础 lib（被 utils 依赖） |
| `examples/workspace-full/libs/utils/utils.z42.toml` | 中间 lib（依赖 core，被 hello 依赖） |
| `examples/workspace-full/apps/hello/hello.z42.toml` | exe（依赖 utils） |
| `examples/workspace-full/libs/core/src/Core.z42` | 样例源 |
| `examples/workspace-full/libs/utils/src/Utils.z42` | 样例源 |
| `examples/workspace-full/apps/hello/src/Main.z42` | 样例源 |

### 修改（MODIFY）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Driver/Program.cs` | subcommand 路由表替换为 `Commands/` 目录下各 Command 类；保留单文件模式 fallback |
| `src/compiler/z42.Compiler/PackageCompiler.cs` | 增加 `CompileMember(member, ctx)` 入口供 orchestrator 调用；保留单工程入口不变 |
| `src/compiler/z42.Project/ManifestErrors.cs` | WS001-007 错误码常量正式启用；移除 C3 标记废弃的 WS004 |
| `src/compiler/z42.Project/ManifestLoader.cs` | 暴露 `LoadWorkspace(rootPath, ctx)` 给 orchestrator；orphan member（WS007）通过 ctx 报告 |
| `docs/design/project.md` | L7 章节"z42c CLI 命令矩阵"；CLI 行为与发现机制 |
| `docs/design/compiler-architecture.md` | WorkspaceBuildOrchestrator + MemberDependencyGraph + IncrementalReusePolicy 原理 |
| `docs/design/error-codes.md` | WS001-007 完整说明 |
| `docs/dev.md` | 构建/测试命令更新（workspace 模式下的常用命令） |
| `docs/roadmap.md` | M6 阶段进度推进（workspace 工具链落地） |

### 删除（DELETE）

无文件删除，但移除 [ManifestErrors.cs](src/compiler/z42.Project/ManifestErrors.cs) 中的 WS004 常量。

### 只读引用

| 文件路径 | 用途 |
|---------|------|
| `src/compiler/z42.Project/ResolvedManifest.cs` | 消费 Origins / EffectiveProductPath（C1+C2+C3 已实施） |
| `src/compiler/z42.Project/CentralizedBuildLayout.cs` | 用于 clean 命令路径计算 |
| `src/compiler/z42.Project/PolicyEnforcer.cs` | 理解 PolicyLocked 来源 |
| `src/compiler/z42.Project/ZpkgReader.cs` / `ZpkgWriter.cs` | 编译产物的读写（不动） |

## Out of Scope

- **lockfile / registry / publish**：与依赖管理体系绑定，future
- **`z42c test` 实际实施**：M7 才落地（依赖测试框架）；C4 仅占位 subcommand
- **`z42c add` / `z42c remove` 操作 dependencies**：future
- **`z42c update`** 类似 cargo update 的依赖刷新：未引入版本管理前不实现
- **远端 trigger / CI 集成**：M6 之后再考虑
- **z42c 自身用 z42 重写**：自举完成前永不
- **JIT/AOT 模式真正运行**：受 M4 限制，C4 仅 `--profile release` 选项可见但 mode=interp（jit/aot 占位）

## Open Questions

无。

## 决策记录摘要

| # | 决策 | 选择 |
|---|------|-----|
| D4.1 | subcommand 命名风格 | Cargo 一致（`build` / `check` / `run` / `clean` / `new` / `fmt` / `metadata` / `tree`） |
| D4.2 | 并行编译实现 | .NET TPL（`Task.WhenAll` 按拓扑层并行）；`--jobs N` 控制 max-degree |
| D4.3 | 增量判定算法 | 三层：source_hash（C1 已存）→ manifest_hash（C4 新增）→ upstream_zpkg_hash（C4 新增） |
| D4.4 | WS006 循环依赖检测 | DFS 着色，错误信息列完整环 |
| D4.5 | `info --resolved` 输出格式 | 人类可读字段表 + 来源链；JSON 走 `metadata` 命令 |
| D4.6 | metadata JSON schema 稳定性 | 加 `"schema_version": 1` 字段，未来 breaking 时 bump |
| D4.7 | 单文件模式（`z42c hello.z42`）保留 | 是；workspace 不存在时自动回退 |
| D4.8 | `z42c build` 在 workspace 根但无 default-members | 编译所有 members（与 Cargo 一致） |
| D4.9 | `z42c run` 仅作用于 exe member | 是；lib member 报错"无 entry，不可运行" |
| D4.10 | `z42c new --workspace` 默认目录布局 | `libs/` + `apps/`；含 `.gitignore` 与示例 manifest |
