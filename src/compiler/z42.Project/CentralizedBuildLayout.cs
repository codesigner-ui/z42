namespace Z42.Project;

/// <summary>
/// Compute the effective output directories for a member build.
///
/// restructure-build-output-dirs (2026-06-06): three-field model.
/// restructure-publish-output-dirs (2026-06-19): four-field model + publish_dir.
///
///     output_dir   = member.OutputDir
///                  ?? workspace.OutputDir
///                  ?? `${workspace_dir}/artifacts/${project_name}/${profile}` (workspace mode)
///                  ?? `${workspace_dir}/artifacts/${profile}` (single-project mode)
///     cache_dir    = member.CacheDir
///                  ?? workspace.CacheDir
///                  ?? `${output_dir}/.cache`
///     dist_dir     = member.DistDir
///                  ?? workspace.DistDir
///                  ?? `${output_dir}/dist`
///     publish_dir  = member.PublishDir
///                  ?? workspace.PublishDir
///                  ?? `${output_dir}/publish`
///
/// In workspace mode, when `cache_dir` is shared across members and the
/// template does not include `${project_name}` / `${member_name}`, the
/// layout appends the member name as a subdirectory automatically to avoid
/// two members stomping each other's intermediate `.zbc` files. The same
/// protection is NOT applied to `dist_dir` / `publish_dir` — distributable
/// products are named `<member>.zpkg` so collisions are already avoided.
///
/// All raw values pass through `PathTemplateExpander` so `${workspace_dir}`
/// / `${profile}` / `${member_name}` / `${project_name}` / `${output_dir}`
/// are interpolated before path resolution. Single-project mode skips
/// `${workspace_dir}` expansion (it has no meaning there).
/// </summary>
public sealed class CentralizedBuildLayout
{
    public sealed record Layout(
        bool   IsCentralized,
        string EffectiveOutputDir,
        string EffectiveCacheDir,
        string EffectiveDistDir,
        string EffectivePublishDir,
        string EffectiveProductPath);

    /// <summary>
    /// Resolve the effective three output paths for a single member.
    /// </summary>
    public Layout Resolve(
        WorkspaceManifest?   workspace,
        string               workspaceRoot,
        string               memberName,
        string               memberDir,
        string               profile,
        BuildSection         memberLocalBuild,
        PathTemplateExpander expander)
    {
        if (workspace is null)
        {
            return ResolveSingleProject(memberName, memberDir, profile, memberLocalBuild, expander);
        }
        return ResolveWorkspace(workspace, workspaceRoot, memberName, memberDir, profile, memberLocalBuild, expander);
    }

    static Layout ResolveSingleProject(
        string               memberName,
        string               memberDir,
        string               profile,
        BuildSection         build,
        PathTemplateExpander expander)
    {
        // Single-project context: no workspace_dir; ${workspace_dir} = memberDir.
        // restructure-publish-output-dirs (2026-06-19): output_dir default changed
        // from memberDir to `artifacts/${profile}` subdir (no project_name level in
        // single-project mode since we're already inside the project directory).
        var ctx = new PathTemplateExpander.Context(
            WorkspaceDir: memberDir,   // ${workspace_dir} in single-project mode = memberDir
            MemberDir:    memberDir,
            MemberName:   memberName,
            Profile:      profile);

        // output_dir: explicit > default `${workspace_dir}/artifacts/${profile}`
        string outputRaw = build.OutputDir ?? "${workspace_dir}/artifacts/${profile}";
        string outputAbs = ExpandAndResolve(outputRaw, ctx, memberDir, expander, "[build].output_dir");

        // cache_dir
        // Resolve relative to memberDir (the z42.toml directory), not outputAbs.
        // Paths using ${output_dir} are already expanded to absolute by ExpandWithOutputDir,
        // so ResolveAbsolute passes them through unchanged regardless of the base.
        string cacheRaw = build.CacheDir ?? "${output_dir}/.cache";
        string cacheExpanded = ExpandWithOutputDir(cacheRaw, ctx, outputAbs, expander, "[build].cache_dir");
        string cacheAbs = ResolveAbsolute(cacheExpanded, memberDir);

        // dist_dir
        string distRaw = build.DistDir ?? "${output_dir}/dist";
        string distExpanded = ExpandWithOutputDir(distRaw, ctx, outputAbs, expander, "[build].dist_dir");
        string distAbs = ResolveAbsolute(distExpanded, memberDir);

        // publish_dir
        string publishRaw = build.PublishDir ?? "${output_dir}/publish";
        string publishExpanded = ExpandWithOutputDir(publishRaw, ctx, outputAbs, expander, "[build].publish_dir");
        string publishAbs = ResolveAbsolute(publishExpanded, memberDir);

        string product = System.IO.Path.Combine(distAbs, $"{memberName}.zpkg");
        return new Layout(
            IsCentralized:        false,
            EffectiveOutputDir:   outputAbs,
            EffectiveCacheDir:    cacheAbs,
            EffectiveDistDir:     distAbs,
            EffectivePublishDir:  publishAbs,
            EffectiveProductPath: product);
    }

    static Layout ResolveWorkspace(
        WorkspaceManifest    workspace,
        string               workspaceRoot,
        string               memberName,
        string               memberDir,
        string               profile,
        BuildSection         memberLocalBuild,
        PathTemplateExpander expander)
    {
        var ctx = new PathTemplateExpander.Context(
            WorkspaceDir: workspaceRoot,
            MemberDir:    memberDir,
            MemberName:   memberName,
            Profile:      profile);

        var wsBuild = workspace.WorkspaceBuild;
        string ownerForLog = workspace.ManifestPath;

        // output_dir: member > workspace > `${workspace_dir}/artifacts/${project_name}/${profile}`
        // restructure-publish-output-dirs (2026-06-19): default changed from workspace_root
        // to artifacts subdirectory to avoid polluting workspace root with build outputs.
        (string outputRaw, string outputField) = memberLocalBuild.OutputDir is { } mo
            ? (mo, "[build].output_dir")
            : wsBuild.OutputDir is { } wo
                ? (wo, "[workspace.build].output_dir")
                : ("${workspace_dir}/artifacts/${project_name}/${profile}", "[workspace.build].output_dir(default)");
        string outputAbs = ExpandAndResolve(outputRaw, ctx, workspaceRoot, expander, outputField, ownerForLog);

        // cache_dir: member > workspace > `${output_dir}/.cache`
        (string cacheRaw, string cacheField, bool cacheIsDefault) = memberLocalBuild.CacheDir is { } mc
            ? (mc, "[build].cache_dir", false)
            : wsBuild.CacheDir is { } wc
                ? (wc, "[workspace.build].cache_dir", false)
                : ("${output_dir}/.cache", "[workspace.build].cache_dir(default)", true);
        string cacheExpanded = ExpandWithOutputDir(cacheRaw, ctx, outputAbs, expander, cacheField, ownerForLog);
        string cacheAbs = ResolveAbsolute(cacheExpanded, outputAbs);
        // Anti-collision fallback: when raw cache template does not include
        // ${member_name} or ${project_name}, append the member name as a subdir
        // so cache files from different members don't overwrite each other.
        if (!cacheRaw.Contains("${member_name}") && !cacheRaw.Contains("${project_name}"))
            cacheAbs = System.IO.Path.Combine(cacheAbs, memberName);

        // dist_dir: member > workspace > `${output_dir}/dist`
        (string distRaw, string distField) = memberLocalBuild.DistDir is { } md
            ? (md, "[build].dist_dir")
            : wsBuild.DistDir is { } wd
                ? (wd, "[workspace.build].dist_dir")
                : ("${output_dir}/dist", "[workspace.build].dist_dir(default)");
        string distExpanded = ExpandWithOutputDir(distRaw, ctx, outputAbs, expander, distField, ownerForLog);
        string distAbs = ResolveAbsolute(distExpanded, outputAbs);

        // publish_dir: member > workspace > `${output_dir}/publish`
        (string publishRaw, string publishField) = memberLocalBuild.PublishDir is { } mp
            ? (mp, "[build].publish_dir")
            : wsBuild.PublishDir is { } wp
                ? (wp, "[workspace.build].publish_dir")
                : ("${output_dir}/publish", "[workspace.build].publish_dir(default)");
        string publishExpanded = ExpandWithOutputDir(publishRaw, ctx, outputAbs, expander, publishField, ownerForLog);
        string publishAbs = ResolveAbsolute(publishExpanded, outputAbs);

        string product = System.IO.Path.Combine(distAbs, $"{memberName}.zpkg");
        return new Layout(
            IsCentralized:        true,
            EffectiveOutputDir:   outputAbs,
            EffectiveCacheDir:    cacheAbs,
            EffectiveDistDir:     distAbs,
            EffectivePublishDir:  publishAbs,
            EffectiveProductPath: product);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    static string ExpandAndResolve(
        string raw, PathTemplateExpander.Context ctx, string baseDir,
        PathTemplateExpander expander, string fieldPath, string? ownerForLog = null)
    {
        string expanded = expander.Expand(raw, ctx, ownerForLog ?? "(synthetic)", fieldPath, PathTemplateExpander.FieldKind.Path);
        return ResolveAbsolute(expanded, baseDir);
    }

    static string ExpandWithOutputDir(
        string raw, PathTemplateExpander.Context ctx, string outputAbs,
        PathTemplateExpander expander, string fieldPath, string? ownerForLog = null)
    {
        // ${output_dir} is resolved BEFORE the expander runs so the
        // expander's standard variable set stays unchanged. Use a literal
        // marker substitution (output_dir is an absolute path; no further
        // expansion needed).
        string preExpanded = raw.Replace("${output_dir}", outputAbs);
        string expanded = expander.Expand(preExpanded, ctx, ownerForLog ?? "(synthetic)", fieldPath, PathTemplateExpander.FieldKind.Path);
        return expanded;
    }

    static string ResolveAbsolute(string path, string baseDir) =>
        System.IO.Path.IsPathRooted(path)
            ? System.IO.Path.GetFullPath(path)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, path));
}
