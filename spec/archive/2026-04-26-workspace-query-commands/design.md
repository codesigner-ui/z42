# Design: workspace 查询命令 + CliOutputFormatter（C4b）

## Architecture

```
z42c <subcommand>
  ├─ info        → InfoCommand (consumes ManifestLoader + ResolvedManifest.Origins)
  ├─ metadata    → MetadataCommand (JSON serializer with schema_version)
  ├─ tree        → TreeCommand (consumes MemberDependencyGraph)
  └─ lint-manifest → LintManifestCommand (calls LoadWorkspace, reports all WSxxx)

z42c <build|info|...>
  └─ throws ManifestException(WSxxx) → CliOutputFormatter.Format(ex)
                                     → console with --pretty (default) or raw text
```

## Decisions

### D4b.1: info 输出格式

**问题**：info / --resolved 用纯文本字段表还是结构化？

**决定**：人类可读字段表，JSON 走 `metadata`。

**理由**：
- 两类用户：开发者（看 info 排查）+ 工具（消费 metadata）
- info 强调可读性（缩进 + 来源链 + 🔒 标记），metadata 强调机读 stability
- 不强行统一让两边都不舒服

### D4b.2: metadata schema_version

**决定**：`"schema_version": "1"`（字符串）

理由：
- 与 `Cargo metadata` 的 `format_version` 一致
- 字符串类型便于将来"1.1" / "2.0" 演进，不限于整数
- IDE 工具可 graceful degradation

### D4b.3: CliOutputFormatter 范围

**决定**：仅 manifest 错误（WSxxx）。

理由：
- 编译错误（Z01xx-Z05xx）已有自己的诊断系统（DiagnosticCatalog），不动
- WSxxx 当前是裸 message，最需要友好化
- 范围聚焦避免改动扩散

### D4b.4: --no-pretty 标志

**决定**：默认友好输出，`--no-pretty` 切换到原 message。

理由：
- 多数交互用户喜欢友好格式
- CI / 脚本可加 `--no-pretty` 避免颜色 / 对齐扰乱日志
- 与 cargo `--message-format=short` 类似

## Implementation Notes

### InfoCommand

```csharp
public sealed class InfoCommand : IZ42Command
{
    public string Name => "info";

    public Task<int> ExecuteAsync(CommandContext ctx, string[] args)
    {
        if (args.Contains("--resolved")) return RenderResolved(ctx, args);
        if (args.Contains("--include-graph")) return RenderIncludeGraph(ctx, args);
        return RenderOverview(ctx);
    }
}
```

`RenderResolved` 输出样例：

```
Workspace root: /repo/z42.workspace.toml
Member:         apps/hello/hello.z42.toml

[project]
  name    = "hello"          ← apps/hello/hello.z42.toml
  kind    = "exe"             ← presets/exe-defaults.toml (via include)
  entry   = "Hello.main"      ← apps/hello/hello.z42.toml
  version = "0.1.0"           ← z42.workspace.toml [workspace.project]
  license = "MIT"             ← z42.workspace.toml [workspace.project]

[build]
  out_dir   = "dist"          ← z42.workspace.toml [policy] 🔒
  cache_dir = ".cache"        ← z42.workspace.toml [policy] 🔒
  mode      = "interp"        ← (default)

[dependencies]
  greeter = "0.1.0"           ← z42.workspace.toml [workspace.dependencies]
                                path: libs/greeter

🔒 = locked by workspace policy
```

### MetadataCommand

```csharp
public sealed record MetadataDto(
    string                  schema_version,
    string                  workspace_root,
    string                  profile,
    IReadOnlyList<MemberDto> members,
    IReadOnlyList<EdgeDto>  dependency_graph);

// 用 System.Text.Json 序列化（不直接序列化 ResolvedManifest 内部类型）
```

JSON 输出示例：

```json
{
  "schema_version": "1",
  "workspace_root": "/repo",
  "profile": "debug",
  "members": [
    {
      "name": "core",
      "path": "/repo/libs/core",
      "kind": "lib",
      "version": "0.1.0",
      "effective_product_path": "/repo/dist/core.zpkg",
      "dependencies": []
    },
    {
      "name": "hello",
      "path": "/repo/apps/hello",
      "kind": "exe",
      "entry": "Hello.main",
      "version": "0.1.0",
      "effective_product_path": "/repo/dist/hello.zpkg",
      "dependencies": ["greeter"]
    }
  ],
  "dependency_graph": [
    { "from": "hello", "to": "greeter" }
  ]
}
```

### TreeCommand

简单 ASCII 树渲染：

```
hello (exe)
└── greeter (lib)
```

实现：从 dependency graph 反推 root（无入边的 member），DFS 印出。

### LintManifestCommand

```csharp
public Task<int> ExecuteAsync(CommandContext ctx, string[] args)
{
    var loader = new ManifestLoader();
    var ws = loader.DiscoverWorkspaceRoot(ctx.WorkingDirectory);

    if (ws is null) {
        // 单工程：单 manifest 校验
        loader.LoadStandalone(...);
        return 0;
    }

    var result = loader.LoadWorkspace(ws);  // 触发所有 WS003-039 校验
    ReportWarnings(result.Warnings);
    return 0;
}
```

不实际编译源码，仅 manifest 层校验。

### CliOutputFormatter

```csharp
public sealed class CliOutputFormatter
{
    public string Format(ManifestException ex)
    {
        // 解析 ex.Message 中的 "error[WSxxx]: ..." 前缀
        // 提取 WS code、title、文件路径、各 hint 行
        // 渲染为带缩进的彩色文本（ANSI escape）
    }

    public string FormatRaw(ManifestException ex) => ex.Message;
}
```

入口（BuildCommand 等）调用：

```csharp
catch (ManifestException ex) {
    var fmt = new CliOutputFormatter();
    Console.Error.WriteLine(opts.NoPretty ? fmt.FormatRaw(ex) : fmt.Format(ex));
    return 1;
}
```

## Testing Strategy

| 测试类 | 覆盖 |
|---|---|
| `InfoCommandTests` | 概览输出 / --resolved 字段表 / Origins 来源标注 / --include-graph |
| `MetadataCommandTests` | schema_version 存在 + 正确 / dependency_graph 边集合 / 单工程模式 |
| `TreeCommandTests` | 单链 / 多顶层 / ASCII 字符正确性 |
| `LintManifestCommandTests` | 触发 WS003 / WS010 / WS020 等错误的 workspace → 报告 |
| `CliOutputFormatterTests` | WS010 / WS024 / WS037 各错误的友好格式 |

## Open Risks

| 风险 | 缓解 |
|---|---|
| metadata JSON 与 Tomlyn 内部类型耦合 | 用独立 DTO（System.Text.Json） |
| ANSI 颜色在 Windows 终端兼容 | 用 `Console.IsOutputRedirected` 自动禁用颜色 |
| --resolved 在 include 链深时输出过长 | 默认折叠 IncludeChain，加 `--verbose` 展开（如有时间）|
