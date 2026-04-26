using Z42.Project;

namespace Z42.Pipeline;

/// <summary>
/// 编排 workspace 跨 member 编译。C4a 范围：
///   - 计算待编译 set（Selected ∪ default-members ∪ all） \ Excluded
///   - 闭包计算（加入传递依赖）
///   - WS001 / WS002 / WS006 检测
///   - 拓扑分层 + 串行遍历（并行留 future）
///   - 调用 PackageCompiler.RunResolved 编译每 member
///   - 上游失败 → 下游 blocked
/// </summary>
public sealed class WorkspaceBuildOrchestrator
{
    public sealed record BuildOptions(
        IReadOnlyList<string> Selected,        // -p flags（空 = 用 default-members 或全部）
        IReadOnlyList<string> Excluded,        // --exclude
        bool                  AllWorkspace,    // --workspace
        bool                  CheckOnly,       // --check-only / 'check' 命令
        bool                  Release,
        bool                  Incremental = true);   // --no-incremental → false

    public sealed record BuildReport(
        IReadOnlyList<string> Succeeded,
        IReadOnlyList<string> Failed,
        IReadOnlyList<string> Blocked)
    {
        public bool AllSucceeded => Failed.Count == 0 && Blocked.Count == 0;
        public int  ExitCode      => AllSucceeded ? 0 : 1;
    }

    /// <summary>
    /// 同步执行（C4a 串行；async 留接口给 future 并行）。
    /// </summary>
    public BuildReport Build(
        WorkspaceLoadResult workspace,
        IReadOnlyList<string> defaultMembers,
        BuildOptions opts)
    {
        // 1. WS001: 重复 member name 检测
        var byName = new Dictionary<string, List<ResolvedManifest>>(StringComparer.Ordinal);
        foreach (var m in workspace.Members)
        {
            if (!byName.TryGetValue(m.MemberName, out var list))
                byName[m.MemberName] = list = new List<ResolvedManifest>();
            list.Add(m);
        }
        foreach (var (name, list) in byName)
        {
            if (list.Count > 1)
                throw Z42Errors.DuplicateMemberName(name, list.Select(m => m.ManifestPath));
        }

        // 2. WS002: -p 与 --exclude 冲突
        var excludedSet = new HashSet<string>(opts.Excluded, StringComparer.Ordinal);
        foreach (var sel in opts.Selected)
        {
            if (excludedSet.Contains(sel))
                throw Z42Errors.ExcludedMemberSelected(sel);
        }

        // 3. 计算初始 set
        IEnumerable<string> initial;
        if (opts.AllWorkspace)
        {
            initial = workspace.Members.Select(m => m.MemberName);
        }
        else if (opts.Selected.Count > 0)
        {
            initial = opts.Selected;
        }
        else if (defaultMembers.Count > 0)
        {
            // default-members 是相对路径（如 "apps/hello"），需要映射到 member name
            var byPath = workspace.Members.ToDictionary(
                m => RelativePath(workspace, m),
                m => m.MemberName,
                StringComparer.Ordinal);
            initial = defaultMembers
                .Where(byPath.ContainsKey)
                .Select(p => byPath[p])
                .ToList();
            // default-members 中未匹配项已由 ManifestLoader.LoadWorkspace 报 WS031
        }
        else
        {
            initial = workspace.Members.Select(m => m.MemberName);
        }

        // 4. 移除 excluded
        var initialSet = initial.Where(n => !excludedSet.Contains(n)).ToList();

        // 5. 验证 -p 指定的 member 存在
        foreach (var sel in opts.Selected)
        {
            if (!byName.ContainsKey(sel))
                throw new ManifestException($"error: member '{sel}' not found in workspace\n" +
                                            $"  known members: {string.Join(", ", byName.Keys)}");
        }

        // 6. 构造依赖图，WS006 环检测
        var graph = new MemberDependencyGraph(workspace.Members);
        var cycle = graph.FindCycle();
        if (cycle is not null)
            throw Z42Errors.CircularDependency(cycle);

        // 7. 闭包：加入传递依赖
        var targets = graph.Closure(initialSet);

        // 8. 拓扑分层
        var layers = graph.TopologicalLayers(targets);

        // 9. 串行遍历每层；上游失败 → 下游 blocked
        var succeeded = new List<string>();
        var failed    = new List<string>();
        var blocked   = new List<string>();

        var byNameSingle = workspace.Members.ToDictionary(m => m.MemberName, StringComparer.Ordinal);

        foreach (var layer in layers)
        {
            foreach (var name in layer)
            {
                // 上游失败 / blocked → 当前 blocked
                bool upstreamBlocked = graph.DirectDependencies(name)
                    .Any(d => failed.Contains(d) || blocked.Contains(d));
                if (upstreamBlocked)
                {
                    blocked.Add(name);
                    continue;
                }

                int exit = CompileMember(byNameSingle[name], opts);
                if (exit == 0) succeeded.Add(name);
                else failed.Add(name);
            }
        }

        return new BuildReport(succeeded, failed, blocked);
    }

    /// <summary>
    /// 调用 PackageCompiler 编译单个 member。可注入 mock 用于测试。
    /// </summary>
    public Func<ResolvedManifest, BuildOptions, int> CompileMember { get; init; } =
        DefaultCompile;

    static int DefaultCompile(ResolvedManifest member, BuildOptions opts)
    {
        return PackageCompiler.RunResolved(member, opts.Release, opts.CheckOnly, opts.Incremental);
    }

    static string RelativePath(WorkspaceLoadResult workspace, ResolvedManifest m)
    {
        if (workspace.Members.Count == 0) return m.MemberName;
        // 推断 workspace 根：所有 manifest 的最近公共祖先
        string root = m.WorkspaceRoot ?? Path.GetDirectoryName(m.ManifestPath)!;
        string memberDir = Path.GetDirectoryName(Path.GetFullPath(m.ManifestPath))!;
        string rel = Path.GetRelativePath(root, memberDir).Replace(Path.DirectorySeparatorChar, '/');
        return rel;
    }
}
