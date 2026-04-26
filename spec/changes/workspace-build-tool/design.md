# Design: z42c workspace 构建工具链（C4）

## Architecture

```
┌────────────────────────────────────────────────────────────┐
│  z42c CLI (Program.cs)                                     │
│  └── 路由：subcommand → Commands/<Name>Command.cs           │
└──────────┬─────────────────────────────────────────────────┘
           │
   ┌───────┴────────────┬──────────────┬──────────────┐
   ▼                    ▼              ▼              ▼
┌─────────┐     ┌────────────┐   ┌──────────┐   ┌───────────┐
│ Build   │     │ Info       │   │ Clean    │   │ New / Fmt │
│ Command │     │ Metadata   │   │ Command  │   │ ...       │
└────┬────┘     │ Tree       │   └────┬─────┘   └───────────┘
     │          │ LintManif. │        │
     │          └────────────┘        │
     ▼                                ▼
┌─────────────────────────────┐   ┌─────────────────────────┐
│ WorkspaceBuildOrchestrator  │   │ CentralizedBuildLayout  │
│  - MemberDependencyGraph    │   │  (C3 已实施)             │
│  - 拓扑排序                  │   └─────────────────────────┘
│  - 并行编译（Task.WhenAll）   │
│  - IncrementalReusePolicy   │
│  - fail-fast / blocked 标记 │
└────────────┬────────────────┘
             │
             ▼
┌─────────────────────────────┐
│ PackageCompiler.CompileMember│
│ (扩展自现有单工程入口)         │
└─────────────────────────────┘
```

## Decisions

### Decision D4.1: Subcommand 命名风格

**问题**：z42c 的命令名遵循 Cargo 还是创新？

**决定**：与 Cargo 一致：`build` / `check` / `run` / `test` / `clean` / `new` / `init` / `fmt` / `metadata` / `tree`。

**理由**：
- 大部分 z42 用户已经会写 Rust；命名一致零学习成本
- 这些动词足够通用，无需创新
- `info` 是 z42 自有命令（Cargo 没有），承担 "查询 workspace + member 配置"

### Decision D4.2: 并行编译实现

**问题**：用什么机制做并行编译？

**选项**：
- A. .NET TPL（`Task.WhenAll`）
- B. 线程池（`Parallel.ForEach`）
- C. 手动管理 `Thread`

**决定**：A。

**理由**：
- TPL 与 z42c 现有异步代码风格一致
- 拓扑层并行很自然：每层调用 `await Task.WhenAll(layerTasks)`
- `--jobs N` 用 `SemaphoreSlim` 限流
- 错误传播自然（任一 task 抛异常会传到 await 处）

### Decision D4.3: 增量判定算法

**问题**：什么情况下跳过 / 重编 / 重链接？

**三层判定**：

```
1. source_hash（现已存在，C1 沿用）
   - 文件级：每个 .z42 文件 SHA-256 是否变化
   - 命中 → 跳过该文件编译，复用 cache zbc

2. manifest_hash（C4 新增）
   - 该 member 的 manifest + 所有 include 文件的 hash 联合
   - 不命中 → 该 member 整体全量重编（继承字段可能影响 codegen）

3. upstream_zpkg_hash（C4 新增）
   - 上游 member 产物的 hash
   - 不命中 → 下游需要重链接（即使源未变，因为依赖元数据变了）
```

**实现要点**：
- manifest_hash 存在 `<cache_dir>/<member>/.manifest_hash`
- upstream_zpkg_hash 在编译时记录到 zpkg 的 `dependencies` 字段（已有，C4 增加 hash 字段）
- 三层任一失效 → 重编/重链；全部命中 → 跳过

### Decision D4.4: WS006 循环依赖检测

**问题**：环检测算法？

**决定**：DFS 三色着色（white / gray / black），撞 gray 节点 → 找到环。

**理由**：
- 经典且简单，O(V+E)
- 错误信息能列完整环路径（沿当前 DFS 栈反推）
- z42 不会有 1k+ member 的极端规模，无需更复杂算法

### Decision D4.5: `info --resolved` 输出格式

**问题**：人类可读 vs 结构化？

**决定**：
- `info --resolved` → 人类可读字段表（含来源链与 🔒 标记）
- `metadata --format json` → 完整 JSON（机器消费）

**理由**：
- 两种用户：人（看 info）+ 工具（消费 metadata）
- 不强行统一会让 info 太啰嗦或 metadata 不全

### Decision D4.6: metadata JSON schema 稳定性

**问题**：JSON 输出是否有版本契约？

**决定**：JSON 顶层加 `"schema_version": 1`；future 任何 breaking 变更必须 bump version。

**理由**：
- IDE / 第三方工具会依赖该 JSON
- 显式版本让消费者能 graceful degradation
- 与 `Cargo metadata` 的 `format_version` 字段一致

### Decision D4.7: 单文件模式保留

**问题**：workspace 工具链落地后，`z42c hello.z42` 单文件模式是否保留？

**决定**：保留。

**理由**：
- 快速调试 / 教学场景刚需
- 单文件模式与 workspace 模式相互不影响
- BuildCommand 入口判断：path 是 `.z42` 源文件 → 单文件；`.z42.toml` → 单工程；CWD 内有 workspace 根 → workspace

### Decision D4.8: `z42c build` 在 workspace 根但无 default-members

**决定**：编译所有 members（与 Cargo 一致）。

### Decision D4.9: `z42c run` 仅作用于 exe member

**决定**：lib member 报错 "no entry, cannot run"。

### Decision D4.10: `z42c new --workspace` 默认布局

**决定**：

```
mymonorepo/
├── z42.workspace.toml      ← 含 [workspace] / [workspace.project] / [workspace.build]
├── .gitignore              ← dist/ / .cache/
├── presets/
│   ├── lib-defaults.toml
│   └── exe-defaults.toml
├── libs/
│   └── (空)
└── apps/
    └── (空)
```

**理由**：开箱即用 + 便于扩展；含 presets 引导用户使用 include 机制。

## Implementation Notes

### Commands/ 目录结构

```
z42.Driver/
├── Program.cs              ← subcommand 路由
├── Commands/
│   ├── CommandContext.cs   ← workspace 根 / profile / verbosity
│   ├── BuildCommand.cs     ← build / check / run（共享 orchestrator）
│   ├── InfoCommand.cs
│   ├── MetadataCommand.cs
│   ├── TreeCommand.cs
│   ├── CleanCommand.cs
│   ├── NewCommand.cs
│   ├── FmtCommand.cs
│   ├── LintManifestCommand.cs
│   └── RunCommand.cs       ← 编译 + 启动 VM 进程
├── CliOutputFormatter.cs   ← 错误码友好输出
└── ...
```

每个 Command 类实现 `IZ42Command`：

```csharp
public interface IZ42Command
{
    string Name { get; }
    Task<int> ExecuteAsync(CommandContext ctx, string[] args);
}
```

### WorkspaceBuildOrchestrator

```csharp
public sealed class WorkspaceBuildOrchestrator
{
    public sealed record BuildOptions(
        IReadOnlyList<string> Selected,    // -p 选定 members（空 → default-members 或 --workspace）
        IReadOnlyList<string> Excluded,    // --exclude
        bool AllWorkspace,                 // --workspace
        bool FailFast,
        int MaxJobs,
        bool Incremental,
        string Profile);

    public async Task<BuildReport> BuildAsync(
        WorkspaceContext workspace,
        BuildOptions opts,
        CancellationToken ct);
}
```

执行流程：
1. 计算待编译 set（Selected ∪ default-members ∪ all） \ Excluded
2. 计算闭包：加入待编译 members 的所有传递依赖
3. 构造 MemberDependencyGraph，DFS 检测环（WS006）
4. 拓扑分层：每层 members 互相独立，可并行
5. 逐层 `await Task.WhenAll(...)`，受 SemaphoreSlim(MaxJobs) 限流
6. 每层成功后才进入下一层；某 member 失败 → 标记其传递下游为 blocked
7. fail-fast 时：第一个失败立即触发 `cts.Cancel()`，所有 task 取消

### MemberDependencyGraph

```csharp
public sealed class MemberDependencyGraph
{
    public sealed record Node(string Name, IReadOnlyList<string> Dependencies);

    public IReadOnlyList<IReadOnlyList<string>> TopologicalLayers();  // 每层并行
    public IReadOnlyList<string>? FindCycle();  // 找到环则返回路径
}
```

### IncrementalReusePolicy

```csharp
public sealed class IncrementalReusePolicy
{
    public sealed record Decision(
        bool ReuseProduct,         // 整产物可复用
        bool RelinkOnly,            // 只需重链接（上游变了）
        IReadOnlyList<string> ChangedSources);  // 需重编源列表

    public Decision Evaluate(
        ResolvedManifest member,
        ZpkgMetadata? lastBuild,
        IReadOnlyDictionary<string, string> upstreamZpkgHashes);
}
```

### CliOutputFormatter

负责把 `ManifestException(WSxxx)` 转为人类友好输出：

```
error[WS010]: PolicyViolation in libs/foo/foo.z42.toml
   ╭─[libs/foo/foo.z42.toml:5:1]
   │
 5 │   [build] out_dir = "custom"
   │           ^^^^^^^ field locked by workspace
   │
   = note: workspace policy locks `build.out_dir = "dist"` at z42.workspace.toml:8
   = help: remove this line or align value with workspace policy
```

风格借鉴 Rust 编译器；CLI 友好但不强制（`--no-pretty` 出最简文本）。

### 测试策略

| 测试类 | 覆盖 |
|---|---|
| `CommandRoutingTests` | subcommand 解析 / 参数边界 |
| `WorkspaceBuildOrchestratorTests` | 拓扑 / 并行 / fail-fast / blocked / WS006 |
| `MemberDependencyGraphTests` | 各种依赖图 / 环 / 自环 |
| `IncrementalReusePolicyTests` | 三层判定 / 命中 / 失效组合 |
| `InfoCommandTests` | `info` / `--resolved` / `--include-graph` 输出 |
| `MetadataCommandTests` | JSON schema / `schema_version` |
| `TreeCommandTests` | ASCII 输出 |
| `CleanCommandTests` | 集中清理 / per-member 清理 |
| `NewCommandTests` | 脚手架生成结构 |
| `FmtCommandTests` | 字段排序 / 缩进 |
| `LintManifestCommandTests` | 静态校验报告 |
| `RunCommandTests` | exe 运行 / lib 报错 |
| `WorkspaceBuildIntegrationTests` | 端到端 `examples/workspace-full/` |

## Open Risks

| 风险 | 缓解 |
|---|---|
| 现有 `z42c build <path>` 单文件模式与 subcommand 路由冲突 | BuildCommand 入口先判断路径形态：`.z42` / `.z42.toml` / 目录 / 不存在 → 各自分派 |
| 并行编译时 cache 写入竞争 | 每个 member 写自己 `<cache_dir>/<member>/`，互不冲突；产物 `dist/<member>.zpkg` 也互斥 |
| `Task.WhenAll` 异常吃多个 task 的失败 | 用 `Task.WhenAll` + 收集所有异常；BuildReport 含完整失败列表 |
| metadata JSON 序列化与 Tomlyn 兼容性 | 自定义 DTO + System.Text.Json，避免直接序列化 manifest 内部类型 |
| Windows 路径分隔符差异 | 统一规范化为 `/`；输出时按平台还原（除 metadata JSON 始终用 `/`）|
| WS004 删除可能破坏其他文档 | C3 阶段已标记 `[Obsolete]`，C4 删除前 `grep -r "WS004"` 检查 |

## C5+ 衔接（future）

- C4 落地后下一批可考虑：lockfile / publish / `z42c add` / `z42c update`
- 当前 C4 已留下 BuildOrchestrator 扩展点：`PreBuildHook` / `PostBuildHook` 接口（占位但 C4 不实现）
- metadata schema_version 机制确保 IDE 集成可平滑演进
