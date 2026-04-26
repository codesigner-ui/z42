# Design: workspace 编译核心运行时（C4a）

## Architecture

```
z42c (Program.cs)
  ├─ workspace 发现：ManifestLoader.DiscoverWorkspaceRoot(CWD)
  ├─ workspace 模式 → BuildCommand
  │     ├─ ManifestLoader.LoadWorkspace(ctx)
  │     ├─ MemberDependencyGraph.Build(members)
  │     │     - 检测环 → WS006
  │     │     - 输出拓扑层
  │     ├─ WorkspaceBuildOrchestrator.BuildAsync(targets, opts)
  │     │     - 串行遍历每个 member
  │     │     - 上游失败 → 下游 blocked
  │     │     - 调用 PackageCompiler.CompileFromResolved(rm, ctx)
  │     └─ 输出 BuildReport（成功/失败/blocked 统计）
  └─ 非 workspace 模式 → 现有路径（不变）
```

## Decisions

### D4a.1: 串行 vs 并行

**问题**：C4a 是否实现并行编译？

**决定**：串行。

**理由**：
- C4a 范围目标是"workspace 模式可用"，并行优化是后续工作
- 串行实现简单（async 方法 await 一个个），易调试
- 并行会引入 SemaphoreSlim 限流 / 错误聚合等复杂度，留 future
- monorepo 编译时间通常不是瓶颈（单 member 编译已秒级）

### D4a.2: 增量复用机制

**问题**：跨 member 增量怎么处理？

**决定**：复用现有 `source_hash`（[ZpkgWriter](src/compiler/z42.Project/ZpkgWriter.cs) 已有），不引入 manifest_hash / upstream_zpkg_hash。

**理由**：
- 现有 source_hash 已能让"源文件未变"的 member 跳过单文件编译
- 跨 member 依赖（一个 member 的 zpkg 变了，下游需要重链）—— 当前 PackageCompiler 默认每次都重读上游 zpkg，行为正确
- 三层 hash 体系是优化，不影响正确性

### D4a.3: check 命令

**问题**：z42c check 是否单独命令？

**决定**：作为 BuildCommand 的 `--check-only` 标志。

**理由**：
- check 与 build 共享 90% 实现（拓扑 + 编译 pipeline），只在写产物处分叉
- 单独 CheckCommand 会重复大量代码
- CLI 入口上 `z42c check` = `z42c build --check-only`（subcommand 路由层做）

### D4a.4: workspace 模式 fallback

**问题**：用户已有单工程项目在 workspace 子树内，怎么强制单工程？

**决定**：`--no-workspace` 显式标志。

**理由**：
- 默认行为：workspace 内的 .z42.toml 自动走 workspace 模式
- 用户需要单独编译某子项目时（不走 workspace 治理）→ `--no-workspace` 跳过发现
- 不引入更复杂机制

## Implementation Notes

### MemberDependencyGraph

```csharp
public sealed class MemberDependencyGraph
{
    public sealed record Node(string Name, IReadOnlyList<string> Dependencies);

    public IReadOnlyList<IReadOnlyList<string>> TopologicalLayers();   // 每层并行 future；C4a 仅串行遍历
    public IReadOnlyList<string>? FindCycle();                          // 环检测，返回完整环路径
}
```

实现：DFS 三色着色（white / gray / black）。撞 gray → 找到环。

### WorkspaceBuildOrchestrator

```csharp
public sealed class WorkspaceBuildOrchestrator
{
    public sealed record BuildOptions(
        IReadOnlyList<string> Selected,
        IReadOnlyList<string> Excluded,
        bool AllWorkspace,
        bool CheckOnly,
        string Profile);

    public sealed record BuildReport(
        IReadOnlyList<string> Succeeded,
        IReadOnlyList<string> Failed,
        IReadOnlyList<string> Blocked);

    public async Task<BuildReport> BuildAsync(
        WorkspaceLoadResult workspace,
        BuildOptions opts,
        CancellationToken ct);
}
```

执行流程：

1. 从 workspace.Members 计算待编译 set（Selected ∪ default ∪ all） \ Excluded
2. 计算闭包：加入传递依赖
3. 检测 WS001 重复 member name / WS002 排除冲突
4. MemberDependencyGraph 构造 + WS006 环检测
5. 拓扑层串行遍历：每个 member 调 PackageCompiler.CompileFromResolved
6. member 失败 → 标记其传递下游为 Blocked
7. 返回 BuildReport

### BuildCommand

```csharp
public sealed class BuildCommand : IZ42Command
{
    public string Name => "build";  // also "check"

    public async Task<int> ExecuteAsync(CommandContext ctx, string[] args)
    {
        var opts = ParseArgs(args);
        if (opts.NoWorkspace || ctx.WorkspaceRoot is null) return ExecuteStandalone(ctx, opts);

        var loader = new ManifestLoader();
        var ws = loader.DiscoverWorkspaceRoot(ctx.WorkingDirectory);
        var result = loader.LoadWorkspace(ws!, opts.Profile);

        var orchestrator = new WorkspaceBuildOrchestrator();
        var report = await orchestrator.BuildAsync(result, opts, ctx.CancellationToken);

        return report.Failed.Count > 0 || report.Blocked.Count > 0 ? 1 : 0;
    }
}
```

### PackageCompiler 新入口

现有签名：`Compile(ProjectManifest, projectDir, releaseFlag, ...)`

新增签名：`CompileFromResolved(ResolvedManifest rm, CompileContext ctx)`

实现：把 ResolvedManifest 上的字段映射到现有 PackageCompiler 内部使用的形式（OutDir → rm.EffectiveOutDir，Sources → rm.Sources，等等），复用 PipelineCore 的 TypeCheck / Codegen。

### CommandContext

```csharp
public sealed record CommandContext(
    string WorkingDirectory,
    string? WorkspaceRoot,
    string Profile,
    int Verbosity,
    CancellationToken CancellationToken);

public interface IZ42Command
{
    string Name { get; }
    Task<int> ExecuteAsync(CommandContext ctx, string[] args);
}
```

### 错误码

```csharp
// ManifestErrors.cs (C4a 追加)
public static ManifestException DuplicateMemberName(string name, IEnumerable<string> paths) =>
    new($"error[{WS001}]: duplicate member name '{name}'\n  files: {string.Join(", ", paths)}");

public static ManifestException ExcludedMemberSelected(string name) =>
    new($"error[{WS002}]: member '{name}' is both selected (-p) and excluded (--exclude)");

public static ManifestException CircularDependency(IReadOnlyList<string> cycle) =>
    new($"error[{WS006}]: circular dependency between workspace members\n  cycle: {string.Join(" -> ", cycle)}");
```

## Open Risks

| 风险 | 缓解 |
|---|---|
| PackageCompiler 现有 API 与 ResolvedManifest 字段不完全对应（如 ExeTargets / ProfileSection） | 仅映射 C4a 必需字段；未实施字段（如多 exe 目标）通过单元测试声明限制 |
| 单工程模式 fallback 行为复杂 | Program.cs 显式判断 + 测试覆盖三模式（workspace / 单工程 / 单文件） |
| BuildCommand 参数解析与现有 CLI flag 冲突 | 与现有 `--release` / path 参数兼容；新增 `--workspace` / `-p` / `--exclude` / `--check-only` / `--no-workspace` |

## C4b/C4c 衔接

- C4a 的 BuildCommand / CommandContext / IZ42Command 接口直接被 C4b/C4c 复用
- C4a 的 WorkspaceBuildOrchestrator 输出 BuildReport，C4b 的 InfoCommand 可消费
- C4a 的 MemberDependencyGraph 被 C4b 的 TreeCommand / MetadataCommand 复用
