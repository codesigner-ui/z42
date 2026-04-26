namespace Z42.Project;

/// <summary>
/// Member 经过 workspace 共享继承（C1）+ include 链合并（C2）+ policy 应用（C3）后的最终配置。
///
/// Origin 类型：
///   C1: MemberDirect / WorkspaceProject / WorkspaceDependency
///   C2: + IncludePreset
///   C3: + PolicyLocked
///
/// C3 新增字段：IsCentralized / EffectiveOutDir / EffectiveCacheDir / EffectiveProductPath，
/// workspace 模式下由 CentralizedBuildLayout 派生；单工程模式下回退到 member-local。
/// </summary>
public sealed record ResolvedManifest(
    string                            MemberName,
    ProjectKind                       Kind,
    string?                           Entry,
    string                            Version,
    IReadOnlyList<string>             Authors,
    string?                           License,
    string?                           Description,
    bool?                             Pack,
    SourcesSection                    Sources,
    BuildSection                      Build,
    IReadOnlyList<ResolvedDependency> Dependencies,
    IReadOnlyDictionary<string, FieldOrigin> Origins,
    string                            ManifestPath,
    string?                           WorkspaceRoot)
{
    // ── C3 集中产物字段 ──────────────────────────────────────────────────────
    /// <summary>true = workspace 模式（产物集中到 workspace 根）；false = 单工程模式。</summary>
    public bool   IsCentralized        { get; init; }
    /// <summary>产物绝对路径目录（已经过 ${profile} 等模板展开）。</summary>
    public string EffectiveOutDir      { get; init; } = "";
    /// <summary>中间产物绝对路径目录（按 member 分子目录）。</summary>
    public string EffectiveCacheDir    { get; init; } = "";
    /// <summary>该 member 产物完整路径（&lt;EffectiveOutDir&gt;/&lt;name&gt;.zpkg）。</summary>
    public string EffectiveProductPath { get; init; } = "";
}

/// <summary>合并后的依赖项（含 workspace 引用展开）。</summary>
public sealed record ResolvedDependency(
    string  Name,
    string  Version,
    string? Path,
    bool    Optional,
    bool    FromWorkspace);

/// <summary>来源类型枚举。C1 仅前三项；C2 加 IncludePreset；C3 加 PolicyLocked。</summary>
public enum OriginKind
{
    /// <summary>由 member 自身 manifest 直接声明。</summary>
    MemberDirect,

    /// <summary>由 [workspace.project] 通过 .workspace = true 引用继承。</summary>
    WorkspaceProject,

    /// <summary>由 [workspace.dependencies] 通过 .workspace = true 引用继承。</summary>
    WorkspaceDependency,

    /// <summary>由 include 链中某 preset 提供（C2 才会出现）。</summary>
    IncludePreset,

    /// <summary>由 workspace [policy] 锁定（C3 才会出现）。</summary>
    PolicyLocked,
}

/// <summary>记录某字段最终值的来源。</summary>
public sealed record FieldOrigin(
    string                 FilePath,
    string                 FieldPath,
    OriginKind             Kind,
    IReadOnlyList<string>? IncludeChain = null);
