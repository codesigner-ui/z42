# Design: workspace 脚手架 + 清理 + WS004 移除（C4c）

## Architecture

```
z42c <subcommand>
  ├─ clean      → CleanCommand (consumes EffectiveOutDir / EffectiveCacheDir)
  ├─ new        → NewCommand (template generator)
  ├─ init       → NewCommand --init mode
  └─ fmt        → FmtCommand (Tomlyn round-trip)
```

## Decisions

### D4c.1: new --workspace 默认布局

**决定**：含 `presets/` 目录与示例 preset 文件，引导用户使用 include 机制。

```
mymonorepo/
├── z42.workspace.toml      （含 [workspace] / [workspace.project] / [workspace.build]）
├── .gitignore              （dist/ / .cache/）
├── presets/
│   ├── lib-defaults.toml   （示例：kind=lib + sources）
│   └── exe-defaults.toml   （示例：kind=exe + sources）
├── libs/                   （空）
└── apps/                   （空）
```

理由：
- 开箱即用 + 引导最佳实践（include 机制）
- presets 目录约定与 examples/workspace-with-presets/ 一致
- 用户可删除/重命名 presets/ 不影响功能

### D4c.2: fmt 是否保留注释

**决定**：保留，用 Tomlyn round-trip API。

理由：
- toml 注释是用户重要语义标注（如 "# 这一行不能改"）
- 破坏注释会让 fmt 命令不被信任、用户不会用
- Tomlyn 支持 round-trip（`TomlSerializer.Deserialize<TomlTable>` + `TomlSerializer.Serialize`）

### D4c.3: init 命令的 manifest 处理

**决定**：保留原 manifest 不动；在父目录创建 z42.workspace.toml + members 列表。

理由：
- 不修改用户已有文件，最小破坏
- 原 manifest 自动成为单一 member
- 用户后续可手动调整 members glob

### D4c.4: WS004 移除时机

**决定**：C4c 一次性删除。

理由：
- C3 已标 `[Obsolete]` 留缓冲（一个 commit 周期）
- C4c 是工作流的"清理段"，符合"pre-1.0 不留兼容"原则
- 推迟到 future 会让 [Obsolete] 长期挂着，增加认知负担

## Implementation Notes

### CleanCommand

```csharp
public sealed class CleanCommand : IZ42Command
{
    public string Name => "clean";

    public Task<int> ExecuteAsync(CommandContext ctx, string[] args)
    {
        var loader = new ManifestLoader();
        var ws = loader.DiscoverWorkspaceRoot(ctx.WorkingDirectory);

        if (ws is not null) return CleanWorkspace(loader, ws, args);
        return CleanStandalone(ctx, args);
    }
}
```

集中清理：删除 `<EffectiveOutDir>` 整棵树 + `<EffectiveCacheDir>` 整棵树。
per-member：仅删 `<out_dir>/<member>.zpkg` + `<cache_dir>/<member>/`。

### NewCommand

模板字符串内联（不依赖外部文件，便于打包）：

```csharp
const string WorkspaceTemplate = """
[workspace]
members = ["libs/*", "apps/*"]

[workspace.project]
version = "0.1.0"
license = "MIT"

[workspace.build]
out_dir   = "dist"
cache_dir = ".cache"
""";

const string LibPresetTemplate = """
[project]
kind = "lib"

[sources]
include = ["src/**/*.z42"]
""";

// 类似 ExePresetTemplate / GitignoreTemplate
```

`new -p <name>` 使用类似模板，但根据 `--kind lib|exe` 选择不同 manifest 模板。

### FmtCommand

```csharp
public Task<int> ExecuteAsync(...)
{
    foreach (var path in EnumerateManifestFiles())
    {
        string original = File.ReadAllText(path);
        var doc = TomlSerializer.Deserialize<TomlTable>(original);  // round-trip
        var sorted = SortFields(doc);
        string formatted = TomlSerializer.Serialize(sorted);
        File.WriteAllText(path, formatted);
    }
    return Task.FromResult(0);
}
```

字段排序规则：
- 顶层段顺序：`[project] / [workspace] / [workspace.project] / [workspace.dependencies] / [workspace.build] / [sources] / [build] / [dependencies] / [profile.*] / [policy]`
- 段内字段：字母序（除非段有"语义顺序"如 `name → version → kind → entry → ...`）

### WS004 移除

```bash
# C4c 实施时
rm -rf 引用 WS004 的代码 / 文档
grep -r "WS004" src/ docs/    # 期望返回空
```

仅 spec/archive 中保留 WS004 历史引用（不动）。

## Testing Strategy

| 测试类 | 覆盖 |
|---|---|
| `CleanCommandTests` | 集中清理（dist/ + .cache/）/ -p / 单工程 / 不存在产物 |
| `NewCommandTests` | --workspace 生成完整结构 / -p lib / -p exe / 已存在目录 / init |
| `FmtCommandTests` | 字段排序 / 注释保留 / 多文件批量 |

## Open Risks

| 风险 | 缓解 |
|---|---|
| Tomlyn round-trip 在某些边界 case 丢失格式 | 测试覆盖关键场景；如丢失则 issue 跟进 |
| WS004 引用残留导致编译错 | grep + dotnet build 双重验证 |
| new -p 与现有 members glob 不匹配 | 生成 manifest 时检查 workspace.members 是否覆盖该路径，否则提示用户更新 glob |
