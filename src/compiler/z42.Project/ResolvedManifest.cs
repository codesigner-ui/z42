namespace Z42.Project;

/// <summary>
/// Member 经过 workspace 共享继承（C1）+ include 链合并（C2）+ policy 应用（C3）后的最终配置。
///
/// C1 范围只产出 MemberDirect / WorkspaceProject / WorkspaceDependency 三种 Origin；
/// C2 增 IncludePreset；C3 增 PolicyLocked。
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
    string?                           WorkspaceRoot);

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
