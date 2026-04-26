namespace Z42.Project;

/// <summary>
/// Manifest 加载入口（C1 范围）。
///
/// 负责：
///   1. workspace 根发现（向上找 z42.workspace.toml；不命中 → 单工程）
///   2. virtual manifest 校验（[project] 与 [workspace] 不可共存 → WS036）
///   3. members 展开（glob + exclude + default-members）
///   4. orphan member 检测（WS007 warning）
///   5. workspace 共享继承（[workspace.project] / [workspace.dependencies]）
///   6. 路径模板变量展开（4 个内置变量）
///   7. 输出 ResolvedManifest（含 Origins 来源链）
///
/// 不在 C1 的：include 链合并（C2）/ policy 强制（C3）。
/// </summary>
public sealed class ManifestLoader
{
    readonly PathTemplateExpander _expander = new();

    /// <summary>
    /// 从给定目录向上查找 z42.workspace.toml；找到 → 加载 workspace；
    /// 走到文件系统根仍未找到 → null（caller 走单工程路径）。
    /// </summary>
    public WorkspaceContext? DiscoverWorkspaceRoot(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "z42.workspace.toml");
            if (File.Exists(candidate))
            {
                var manifest = WorkspaceManifest.Load(candidate);
                return new WorkspaceContext(manifest);
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// 加载 workspace 全部 members。返回每个 member 的 ResolvedManifest。
    /// </summary>
    public WorkspaceLoadResult LoadWorkspace(WorkspaceContext ctx, string profile = "debug")
    {
        var expander = new GlobExpander();
        var locations = expander.Expand(
            ctx.Manifest.RootDirectory,
            ctx.Manifest.MembersPatterns,
            ctx.Manifest.ExcludePatterns);

        // default-members 校验（WS031）
        if (ctx.Manifest.DefaultMembers.Count > 0)
        {
            var memberDirs = locations
                .Select(l => Path.GetRelativePath(ctx.Manifest.RootDirectory, l.DirectoryPath)
                                 .Replace(Path.DirectorySeparatorChar, '/'))
                .ToHashSet(StringComparer.Ordinal);

            var unknown = ctx.Manifest.DefaultMembers.Where(d => !memberDirs.Contains(d)).ToList();
            if (unknown.Count > 0)
                throw Z42Errors.InvalidDefaultMembers(unknown, memberDirs);
        }

        // 加载每个 member 的 ResolvedManifest
        var resolved = new List<ResolvedManifest>();
        var warnings = new List<ManifestException>();

        foreach (var loc in locations)
        {
            var member = MemberManifest.Load(loc.ManifestPath);
            var rm = ResolveMember(member, ctx, profile);
            resolved.Add(rm);
        }

        // orphan member 检测：扫描 workspace 子树中所有 *.z42.toml，看是否被 locations 命中
        // 仅作 warning（C1 阶段不阻塞构建）
        DetectOrphans(ctx.Manifest.RootDirectory, locations, warnings);

        return new WorkspaceLoadResult(resolved, warnings);
    }

    /// <summary>
    /// 单工程模式：加载单个 member manifest（无 workspace 上下文，不应用共享继承）。
    /// </summary>
    public ResolvedManifest LoadStandalone(string memberPath, string profile = "debug")
    {
        var member = MemberManifest.Load(memberPath);
        return ResolveMember(member, workspace: null, profile);
    }

    // ── 共享继承与字段合并 ───────────────────────────────────────────────────

    ResolvedManifest ResolveMember(MemberManifest member, WorkspaceContext? workspace, string profile)
    {
        var origins = new Dictionary<string, FieldOrigin>(StringComparer.Ordinal);
        string memberDir = Path.GetDirectoryName(Path.GetFullPath(member.ManifestPath))!;
        string workspaceDir = workspace?.Manifest.RootDirectory ?? memberDir;

        // C2: include 链展开 + 合并
        var ctxForInclude = new PathTemplateExpander.Context(
            WorkspaceDir: workspaceDir,
            MemberDir:    memberDir,
            MemberName:   member.Project.Name ?? "",
            Profile:      profile);
        var includeResolver = new IncludeResolver(_expander, ctxForInclude);
        var presets = includeResolver.Resolve(member);

        Dictionary<string, string> presetOrigins;
        if (presets.Count > 0)
        {
            var merger = new ManifestMerger();
            var mergeResult = merger.Merge(presets, member);
            member = mergeResult.Merged;
            presetOrigins = (Dictionary<string, string>)mergeResult.PresetOrigins;
        }
        else
        {
            presetOrigins = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // member.Project.Name 必须在合并后存在（否则身份字段缺失）
        if (string.IsNullOrEmpty(member.Project.Name))
            throw new ManifestException($"error: [project].name is required in {member.ManifestPath}");

        var ctx = new PathTemplateExpander.Context(
            WorkspaceDir: workspaceDir,
            MemberDir:    memberDir,
            MemberName:   member.Project.Name,
            Profile:      profile);

        // [project] 字段：检测 .workspace = true 引用并展开
        string version = ResolveSharedField(
            member.Project.Version, "version",
            workspace?.Manifest.WorkspaceProject?.Version,
            workspace, member.ManifestPath, origins, presetOrigins) ?? "0.1.0";

        var authors = ResolveSharedListField(
            member.Project.Authors, "authors",
            workspace?.Manifest.WorkspaceProject?.Authors,
            workspace, member.ManifestPath, origins, presetOrigins) ?? Array.Empty<string>();

        string? license = ResolveSharedField(
            member.Project.License, "license",
            workspace?.Manifest.WorkspaceProject?.License,
            workspace, member.ManifestPath, origins, presetOrigins);

        string? description = ResolveSharedField(
            member.Project.Description, "description",
            workspace?.Manifest.WorkspaceProject?.Description,
            workspace, member.ManifestPath, origins, presetOrigins);

        // [project] 标量字段（非可共享）：name / kind / entry / pack
        // 来源标注：若来自 preset 则 IncludePreset，否则 MemberDirect
        StampOrigin(origins, "[project].name",  member.ManifestPath, OriginKind.MemberDirect);
        StampOriginWithPresetOrSelf(origins, presetOrigins, "[project].kind",  member.ManifestPath);
        if (member.Project.Entry is not null)
            StampOrigin(origins, "[project].entry", member.ManifestPath, OriginKind.MemberDirect);

        // 名字、kind、entry 的标量值不允许变量替换（WS039 由 expander 守卫）
        _ = _expander.Expand(member.Project.Name,    ctx, member.ManifestPath, "[project].name", PathTemplateExpander.FieldKind.Scalar);
        if (member.Project.Entry is not null)
            _ = _expander.Expand(member.Project.Entry, ctx, member.ManifestPath, "[project].entry", PathTemplateExpander.FieldKind.Scalar);
        _ = _expander.Expand(version, ctx, member.ManifestPath, "[project].version", PathTemplateExpander.FieldKind.Scalar);

        // [sources] include / exclude：路径字段，允许模板。member.Sources 为 null → 默认值
        var sourcesView = member.Sources ?? new SourcesSection(["src/**/*.z42"], []);
        var sourcesInclude = ExpandPathArray(sourcesView.Include, ctx, member.ManifestPath, "[sources].include");
        var sourcesExclude = ExpandPathArray(sourcesView.Exclude, ctx, member.ManifestPath, "[sources].exclude");

        // [build]：member 自己的（C1 不集中处理；C3 改）
        var build = member.Build ?? new BuildSection("dist", "interp", true);
        // build 字段路径展开（C1 仅 out_dir 路径）
        string buildOutDir = _expander.Expand(build.OutDir, ctx, member.ManifestPath, "[build].out_dir", PathTemplateExpander.FieldKind.Path);
        build = build with { OutDir = buildOutDir };

        // [dependencies]：合并（含 workspace 引用展开）
        var resolvedDeps = ResolveDependencies(member, workspace, origins, presetOrigins);

        // Kind 默认 Exe（与 ProjectManifest 行为一致）
        var resolvedKind = member.Project.Kind ?? ProjectKind.Exe;

        return new ResolvedManifest(
            MemberName:    member.Project.Name,
            Kind:          resolvedKind,
            Entry:         member.Project.Entry,
            Version:       version,
            Authors:       authors,
            License:       license,
            Description:   description,
            Pack:          member.Project.Pack,
            Sources:       new SourcesSection(sourcesInclude, sourcesExclude),
            Build:         build,
            Dependencies:  resolvedDeps,
            Origins:       origins,
            ManifestPath:  member.ManifestPath,
            WorkspaceRoot: workspace?.Manifest.RootDirectory);
    }

    string? ResolveSharedField(
        FieldRef<string>? memberRef,
        string fieldName,
        string? workspaceValue,
        WorkspaceContext? workspace,
        string memberPath,
        Dictionary<string, FieldOrigin> origins,
        IReadOnlyDictionary<string, string> presetOrigins)
    {
        string fieldPath = $"[project].{fieldName}";
        if (memberRef is null) return null;

        if (memberRef.UsesWorkspace)
        {
            if (workspace is null || workspaceValue is null)
                throw Z42Errors.WorkspaceFieldNotFound(memberPath, fieldName);

            origins[fieldPath] = new FieldOrigin(workspace.Manifest.ManifestPath, fieldPath, OriginKind.WorkspaceProject);
            return workspaceValue;
        }

        // 直接值：来自 preset 还是 self?
        StampOriginWithPresetOrSelf(origins, presetOrigins, fieldPath, memberPath);
        return memberRef.Value;
    }

    IReadOnlyList<string>? ResolveSharedListField(
        FieldRef<IReadOnlyList<string>>? memberRef,
        string fieldName,
        IReadOnlyList<string>? workspaceValue,
        WorkspaceContext? workspace,
        string memberPath,
        Dictionary<string, FieldOrigin> origins,
        IReadOnlyDictionary<string, string> presetOrigins)
    {
        string fieldPath = $"[project].{fieldName}";
        if (memberRef is null) return null;

        if (memberRef.UsesWorkspace)
        {
            if (workspace is null || workspaceValue is null)
                throw Z42Errors.WorkspaceFieldNotFound(memberPath, fieldName);
            origins[fieldPath] = new FieldOrigin(workspace.Manifest.ManifestPath, fieldPath, OriginKind.WorkspaceProject);
            return workspaceValue;
        }

        StampOriginWithPresetOrSelf(origins, presetOrigins, fieldPath, memberPath);
        return memberRef.Value;
    }

    IReadOnlyList<ResolvedDependency> ResolveDependencies(
        MemberManifest member,
        WorkspaceContext? workspace,
        Dictionary<string, FieldOrigin> origins,
        IReadOnlyDictionary<string, string> presetOrigins)
    {
        var result = new List<ResolvedDependency>();
        if (member.Dependencies is null) return result;
        foreach (var dep in member.Dependencies)
        {
            string fieldPath = $"[dependencies].{dep.Name}";

            if (dep.UsesWorkspace)
            {
                if (workspace is null
                    || !workspace.Manifest.WorkspaceDependencies.TryGetValue(dep.Name, out var wsDep))
                {
                    throw Z42Errors.WorkspaceDependencyNotFound(member.ManifestPath, dep.Name);
                }
                origins[fieldPath] = new FieldOrigin(workspace.Manifest.ManifestPath, fieldPath, OriginKind.WorkspaceDependency);
                result.Add(new ResolvedDependency(
                    Name:          dep.Name,
                    Version:       wsDep.Version,
                    Path:          wsDep.Path,
                    Optional:      dep.Optional,
                    FromWorkspace: true));
            }
            else
            {
                // 检查是否来自 preset（来自 IncludePreset 来源）
                if (presetOrigins.TryGetValue(fieldPath, out var presetPath))
                {
                    origins[fieldPath] = new FieldOrigin(presetPath, fieldPath, OriginKind.IncludePreset);
                }
                else
                {
                    origins[fieldPath] = new FieldOrigin(member.ManifestPath, fieldPath, OriginKind.MemberDirect);
                }
                result.Add(new ResolvedDependency(
                    Name:          dep.Name,
                    Version:       dep.DirectVersion ?? "*",
                    Path:          dep.DirectPath,
                    Optional:      dep.Optional,
                    FromWorkspace: false));
            }
        }
        return result;
    }

    IReadOnlyList<string> ExpandPathArray(
        IReadOnlyList<string> items,
        PathTemplateExpander.Context ctx,
        string filePath,
        string fieldPath)
    {
        var result = new List<string>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            string itemFieldPath = $"{fieldPath}[{i}]";
            result.Add(_expander.Expand(items[i], ctx, filePath, itemFieldPath, PathTemplateExpander.FieldKind.Path));
        }
        return result;
    }

    static void TrackOrigin(Dictionary<string, FieldOrigin> origins, string fieldPath, string filePath, OriginKind kind)
    {
        origins[fieldPath] = new FieldOrigin(filePath, fieldPath, kind);
    }

    static void StampOrigin(Dictionary<string, FieldOrigin> origins, string fieldPath, string filePath, OriginKind kind)
        => TrackOrigin(origins, fieldPath, filePath, kind);

    static void StampOriginWithPresetOrSelf(
        Dictionary<string, FieldOrigin> origins,
        IReadOnlyDictionary<string, string> presetOrigins,
        string fieldPath,
        string memberPath)
    {
        if (presetOrigins.TryGetValue(fieldPath, out var presetPath))
        {
            origins[fieldPath] = new FieldOrigin(presetPath, fieldPath, OriginKind.IncludePreset);
        }
        else
        {
            origins[fieldPath] = new FieldOrigin(memberPath, fieldPath, OriginKind.MemberDirect);
        }
    }

    void DetectOrphans(
        string rootDir,
        IReadOnlyList<GlobExpander.MemberLocation> locations,
        List<ManifestException> warnings)
    {
        var matched = locations.Select(l => Path.GetFullPath(l.ManifestPath)).ToHashSet(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(rootDir, "*.z42.toml", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("z42.workspace.toml", StringComparison.Ordinal)) continue;
            string full = Path.GetFullPath(file);
            if (matched.Contains(full)) continue;
            warnings.Add(Z42Errors.OrphanMember(full, rootDir));
        }
    }
}

/// <summary>Workspace 上下文：含已解析的 root manifest。</summary>
public sealed record WorkspaceContext(WorkspaceManifest Manifest);

/// <summary>Workspace 加载结果：所有 members + warnings。</summary>
public sealed record WorkspaceLoadResult(
    IReadOnlyList<ResolvedManifest> Members,
    IReadOnlyList<ManifestException> Warnings);
