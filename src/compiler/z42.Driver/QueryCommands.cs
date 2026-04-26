using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Z42.Pipeline;
using Z42.Project;

namespace Z42.Driver;

/// <summary>
/// C4b 查询命令：info / metadata / tree / lint-manifest。
/// 与 BuildCommand 一样使用 static class + System.CommandLine 风格。
/// </summary>
static class QueryCommands
{
    // ── info ─────────────────────────────────────────────────────────────────

    public static Command CreateInfo()
    {
        var cmd          = new Command("info", "Show workspace info or resolved member configuration");
        var resolvedOpt  = new Option<bool>("--resolved", "Show resolved manifest with field origins for -p");
        var graphOpt     = new Option<bool>("--include-graph", "Show include chain for -p");
        var packagesOpt  = new Option<string?>(["-p", "--package"], "Member to inspect (required with --resolved / --include-graph)");

        cmd.AddOption(resolvedOpt);
        cmd.AddOption(graphOpt);
        cmd.AddOption(packagesOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var resolved = ctx.ParseResult.GetValueForOption(resolvedOpt);
            var graph    = ctx.ParseResult.GetValueForOption(graphOpt);
            var pkg      = ctx.ParseResult.GetValueForOption(packagesOpt);
            ctx.ExitCode = RunInfo(resolved, graph, pkg);
        });

        return cmd;
    }

    static int RunInfo(bool resolved, bool graph, string? pkg)
    {
        try
        {
            var loader = new ManifestLoader();
            var ws = loader.DiscoverWorkspaceRoot(Directory.GetCurrentDirectory());

            if (ws is null)
            {
                Console.Out.WriteLine("Standalone mode (no workspace).");
                Console.Out.WriteLine("CWD: " + Directory.GetCurrentDirectory());
                return 0;
            }

            var result = loader.LoadWorkspace(ws);

            if (resolved || graph)
            {
                if (pkg is null)
                {
                    Console.Error.WriteLine("error: --resolved / --include-graph requires -p <name>");
                    return 1;
                }
                var member = result.Members.FirstOrDefault(m => m.MemberName == pkg);
                if (member is null)
                {
                    Console.Error.WriteLine($"error: member '{pkg}' not found");
                    return 1;
                }
                if (resolved) RenderResolved(ws, member);
                if (graph) RenderIncludeGraph(member);
                return 0;
            }

            return RenderOverview(ws, result);
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(CliOutputFormatter.Format(ex, pretty: true));
            return 1;
        }
    }

    static int RenderOverview(WorkspaceContext ws, WorkspaceLoadResult result)
    {
        Console.Out.WriteLine($"Workspace root: {ws.Manifest.ManifestPath}");
        Console.Out.WriteLine($"Members: {result.Members.Count}");
        foreach (var m in result.Members.OrderBy(m => m.MemberName, StringComparer.Ordinal))
        {
            string entry = m.Entry is null ? "" : $", entry={m.Entry}";
            Console.Out.WriteLine($"  - {m.MemberName} ({m.Kind.ToString().ToLowerInvariant()}{entry}) at {Path.GetRelativePath(ws.Manifest.RootDirectory, m.ManifestPath)}");
        }
        if (ws.Manifest.DefaultMembers.Count > 0)
            Console.Out.WriteLine($"Default members: {string.Join(", ", ws.Manifest.DefaultMembers)}");
        if (result.Warnings.Count > 0)
            Console.Out.WriteLine($"Warnings: {result.Warnings.Count}");
        return 0;
    }

    static void RenderResolved(WorkspaceContext ws, ResolvedManifest m)
    {
        Console.Out.WriteLine($"Workspace root: {ws.Manifest.ManifestPath}");
        Console.Out.WriteLine($"Member:         {m.ManifestPath}");
        Console.Out.WriteLine();
        Console.Out.WriteLine("[project]");
        WriteField("name",        m.MemberName,                                  m, "[project].name");
        WriteField("kind",        m.Kind.ToString().ToLowerInvariant(),          m, "[project].kind");
        if (m.Entry is not null) WriteField("entry", m.Entry,                    m, "[project].entry");
        WriteField("version",     m.Version,                                     m, "[project].version");
        if (m.License is not null)     WriteField("license",     m.License,      m, "[project].license");
        if (m.Description is not null) WriteField("description", m.Description,  m, "[project].description");

        Console.Out.WriteLine();
        Console.Out.WriteLine("[build]");
        WriteField("out_dir",   m.Build.OutDir,         m, "build.out_dir");
        WriteField("cache_dir", "(workspace policy)",   m, "build.cache_dir");   // 实际 dir 在 EffectiveCacheDir
        WriteField("mode",      m.Build.Mode,           m, "build.mode");

        if (m.IsCentralized)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("[centralized layout]");
            Console.Out.WriteLine($"  effective_out_dir      = {m.EffectiveOutDir}");
            Console.Out.WriteLine($"  effective_cache_dir    = {m.EffectiveCacheDir}");
            Console.Out.WriteLine($"  effective_product_path = {m.EffectiveProductPath}");
        }

        if (m.Dependencies.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("[dependencies]");
            foreach (var dep in m.Dependencies)
            {
                string suffix = dep.FromWorkspace ? " (from workspace)" : "";
                Console.Out.WriteLine($"  {dep.Name} = \"{dep.Version}\"{suffix}");
            }
        }

        if (m.Origins.Values.Any(o => o.Kind == OriginKind.PolicyLocked))
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("🔒 = locked by workspace policy");
        }
    }

    static void WriteField(string name, string value, ResolvedManifest m, string fieldPath)
    {
        string origin = "";
        string lockMark = "";
        if (m.Origins.TryGetValue(fieldPath, out var fo))
        {
            origin = fo.Kind switch
            {
                OriginKind.MemberDirect        => "(member)",
                OriginKind.WorkspaceProject    => $"(workspace.project @ {Path.GetFileName(fo.FilePath)})",
                OriginKind.WorkspaceDependency => $"(workspace.dependencies @ {Path.GetFileName(fo.FilePath)})",
                OriginKind.IncludePreset       => $"(preset @ {Path.GetFileName(fo.FilePath)})",
                OriginKind.PolicyLocked        => "(policy)",
                _                               => ""
            };
            if (fo.Kind == OriginKind.PolicyLocked) lockMark = " 🔒";
        }
        Console.Out.WriteLine($"  {name,-12} = \"{value}\"{lockMark}  {origin}");
    }

    static void RenderIncludeGraph(ResolvedManifest m)
    {
        // C4b 阶段：include 链信息暴露在 Origins.IncludeChain 中
        var chains = m.Origins.Values
            .Where(o => o.Kind == OriginKind.IncludePreset && o.IncludeChain is { Count: > 0 })
            .Select(o => o.IncludeChain!)
            .Distinct(new ChainEqualityComparer())
            .ToList();

        Console.Out.WriteLine($"Include graph for {m.MemberName}:");
        if (chains.Count == 0)
        {
            Console.Out.WriteLine("  (no include presets)");
            return;
        }
        foreach (var chain in chains)
        {
            for (int i = 0; i < chain.Count; i++)
                Console.Out.WriteLine(new string(' ', i * 2) + "└── " + Path.GetFileName(chain[i]));
        }
    }

    sealed class ChainEqualityComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
            => x is not null && y is not null && x.SequenceEqual(y, StringComparer.Ordinal);
        public int GetHashCode(IReadOnlyList<string> obj)
            => obj.Aggregate(0, (h, s) => h ^ s.GetHashCode(StringComparison.Ordinal));
    }

    // ── metadata ─────────────────────────────────────────────────────────────

    public static Command CreateMetadata()
    {
        var cmd       = new Command("metadata", "Print machine-readable workspace metadata (JSON)");
        var formatOpt = new Option<string>("--format", () => "json", "Output format (currently only 'json')");
        cmd.AddOption(formatOpt);
        cmd.SetHandler((InvocationContext ctx) =>
        {
            string fmt = ctx.ParseResult.GetValueForOption(formatOpt) ?? "json";
            ctx.ExitCode = RunMetadata(fmt);
        });
        return cmd;
    }

    sealed record EdgeDto(string from, string to);

    sealed record DependencyDto(string name, string version, string? path, bool from_workspace);

    sealed record MemberDto(
        string name,
        string path,
        string kind,
        string? entry,
        string version,
        string effective_product_path,
        IReadOnlyList<DependencyDto> dependencies);

    sealed record MetadataDto(
        string schema_version,
        string workspace_root,
        IReadOnlyList<MemberDto> members,
        IReadOnlyList<EdgeDto> dependency_graph);

    static int RunMetadata(string fmt)
    {
        if (fmt != "json")
        {
            Console.Error.WriteLine($"error: only 'json' format supported, got '{fmt}'");
            return 1;
        }
        try
        {
            var loader = new ManifestLoader();
            var ws = loader.DiscoverWorkspaceRoot(Directory.GetCurrentDirectory());

            if (ws is null)
            {
                var dto = new MetadataDto("1", Directory.GetCurrentDirectory(), [], []);
                Console.Out.WriteLine(JsonSerializer.Serialize(dto, JsonOpts));
                return 0;
            }

            var result = loader.LoadWorkspace(ws);
            var graph = new MemberDependencyGraph(result.Members);

            var members = result.Members.Select(m => new MemberDto(
                name:                   m.MemberName,
                path:                   m.ManifestPath,
                kind:                   m.Kind.ToString().ToLowerInvariant(),
                entry:                  m.Entry,
                version:                m.Version,
                effective_product_path: m.EffectiveProductPath,
                dependencies:           m.Dependencies.Select(d => new DependencyDto(d.Name, d.Version, d.Path, d.FromWorkspace)).ToList()
            )).ToList();

            var edges = graph.Edges()
                .Select(e => new EdgeDto(e.From, e.To))
                .ToList();

            var meta = new MetadataDto("1", ws.Manifest.RootDirectory, members, edges);
            Console.Out.WriteLine(JsonSerializer.Serialize(meta, JsonOpts));
            return 0;
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(CliOutputFormatter.Format(ex, pretty: true));
            return 1;
        }
    }

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ── tree ─────────────────────────────────────────────────────────────────

    public static Command CreateTree()
    {
        var cmd = new Command("tree", "Show workspace member dependency tree");
        cmd.SetHandler((InvocationContext ctx) => ctx.ExitCode = RunTree());
        return cmd;
    }

    static int RunTree()
    {
        try
        {
            var loader = new ManifestLoader();
            var ws = loader.DiscoverWorkspaceRoot(Directory.GetCurrentDirectory());
            if (ws is null) { Console.Error.WriteLine("error: not in a workspace"); return 1; }

            var result = loader.LoadWorkspace(ws);
            var graph = new MemberDependencyGraph(result.Members);

            // 找根（无入边的节点）
            var hasIncoming = new HashSet<string>(graph.Edges().Select(e => e.To), StringComparer.Ordinal);
            var roots = graph.AllMembers().Where(m => !hasIncoming.Contains(m)).OrderBy(s => s, StringComparer.Ordinal).ToList();

            foreach (var root in roots)
                PrintTree(graph, root, "", true);

            return 0;
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(CliOutputFormatter.Format(ex, pretty: true));
            return 1;
        }
    }

    static void PrintTree(MemberDependencyGraph graph, string node, string indent, bool isLast, HashSet<string>? visiting = null)
    {
        visiting ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visiting.Add(node))
        {
            Console.Out.WriteLine(indent + (isLast ? "└── " : "├── ") + node + " <cycle>");
            return;
        }

        Console.Out.WriteLine(indent + (isLast ? "└── " : "├── ") + node);
        var deps = graph.DirectDependencies(node);
        for (int i = 0; i < deps.Count; i++)
        {
            bool last = i == deps.Count - 1;
            string newIndent = indent + (isLast ? "    " : "│   ");
            PrintTree(graph, deps[i], newIndent, last, visiting);
        }
        visiting.Remove(node);
    }

    // ── lint-manifest ────────────────────────────────────────────────────────

    public static Command CreateLintManifest()
    {
        var cmd = new Command("lint-manifest", "Statically validate workspace and member manifests");
        cmd.SetHandler((InvocationContext ctx) => ctx.ExitCode = RunLintManifest());
        return cmd;
    }

    static int RunLintManifest()
    {
        try
        {
            var loader = new ManifestLoader();
            var ws = loader.DiscoverWorkspaceRoot(Directory.GetCurrentDirectory());

            if (ws is null)
            {
                Console.Out.WriteLine("manifest OK (standalone mode)");
                return 0;
            }

            var result = loader.LoadWorkspace(ws);
            int warnCount = result.Warnings.Count;

            foreach (var w in result.Warnings)
                Console.Error.WriteLine(CliOutputFormatter.Format(w, pretty: true));

            Console.Out.WriteLine($"manifest OK ({result.Members.Count} member(s), {warnCount} warning(s))");
            return 0;
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(CliOutputFormatter.Format(ex, pretty: true));
            return 1;
        }
    }
}
