namespace Z42.Project;

/// <summary>
/// 计算 workspace 模式下 member 产物路径与 cache 路径。
///
/// 默认布局（D3.3）：
///   产物：&lt;workspace_root&gt;/&lt;out_dir&gt;/&lt;member&gt;.zpkg     （一层）
///   cache: &lt;workspace_root&gt;/&lt;cache_dir&gt;/&lt;member&gt;/    （按 member 分目录）
///
/// out_dir / cache_dir 支持模板变量：${workspace_dir} / ${profile} / ${member_name}。
/// 单工程模式（workspace=null）走 member-local 兼容路径。
/// </summary>
public sealed class CentralizedBuildLayout
{
    public sealed record Layout(
        bool   IsCentralized,
        string EffectiveOutDir,
        string EffectiveCacheDir,
        string EffectiveProductPath);

    /// <summary>
    /// 计算单个 member 的产物与 cache 路径。
    /// </summary>
    public Layout Resolve(
        WorkspaceManifest? workspace,
        string             workspaceRoot,
        string             memberName,
        string             memberDir,
        string             profile,
        BuildSection       memberLocalBuild,
        PathTemplateExpander expander)
    {
        if (workspace is null)
        {
            // 单工程模式：用 member-local 配置（与 ProjectManifest 行为一致）
            string localOut = Path.GetFullPath(Path.Combine(memberDir, memberLocalBuild.OutDir));
            string localCache = Path.GetFullPath(Path.Combine(memberDir, ".cache"));
            return new Layout(
                IsCentralized:        false,
                EffectiveOutDir:      localOut,
                EffectiveCacheDir:    localCache,
                EffectiveProductPath: Path.Combine(localOut, $"{memberName}.zpkg"));
        }

        // workspace 模式：从 [workspace.build] 派生
        var ctx = new PathTemplateExpander.Context(
            WorkspaceDir: workspaceRoot,
            MemberDir:    memberDir,
            MemberName:   memberName,
            Profile:      profile);

        string outDirRaw   = workspace.WorkspaceBuild.OutDir;
        string cacheDirRaw = workspace.WorkspaceBuild.CacheDir;

        string outExpanded   = expander.Expand(outDirRaw,   ctx, workspace.ManifestPath, "[workspace.build].out_dir",   PathTemplateExpander.FieldKind.Path);
        string cacheExpanded = expander.Expand(cacheDirRaw, ctx, workspace.ManifestPath, "[workspace.build].cache_dir", PathTemplateExpander.FieldKind.Path);

        string outAbs = Path.IsPathRooted(outExpanded)
            ? Path.GetFullPath(outExpanded)
            : Path.GetFullPath(Path.Combine(workspaceRoot, outExpanded));

        string cacheAbs = Path.IsPathRooted(cacheExpanded)
            ? Path.GetFullPath(cacheExpanded)
            : Path.GetFullPath(Path.Combine(workspaceRoot, cacheExpanded));

        string product = Path.Combine(outAbs, $"{memberName}.zpkg");
        string memberCache = Path.Combine(cacheAbs, memberName);

        return new Layout(
            IsCentralized:        true,
            EffectiveOutDir:      outAbs,
            EffectiveCacheDir:    memberCache,
            EffectiveProductPath: product);
    }
}
