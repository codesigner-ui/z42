namespace Z42.Project;

/// <summary>
/// 把 include 链中的 preset 列表合并到 self member 上，输出"已合并"的 MemberManifest。
///
/// 合并规则（D2.2 决策）：
///   - 标量字段：后写者覆盖前写者；自身（self）覆盖所有 preset
///   - 表字段（如 [project]）：字段级合并；同名字段后者覆盖
///   - 数组字段（如 [sources].include）：整体覆盖（不连接）
///   - Dependencies：按 name key 字典合并；同 name 后者整体替换
/// </summary>
public sealed class ManifestMerger
{
    /// <summary>
    /// 合并 preset 列表 + self。preset 按合并顺序排列（先合并的先在前），self 最后覆盖。
    ///
    /// origins 字典记录每个字段的最终来源（IncludePreset 或 MemberDirect）。
    /// </summary>
    public sealed record MergeResult(
        MemberManifest                 Merged,
        IReadOnlyDictionary<string, string> PresetOrigins);  // fieldPath → preset 文件路径

    public MergeResult Merge(IReadOnlyList<MemberManifest> presets, MemberManifest self)
    {
        var presetOrigins = new Dictionary<string, string>(StringComparer.Ordinal);

        // 起始空 manifest，逐层合并
        var acc = new MergeAccumulator
        {
            Project           = MemberProject.Empty(),
            Sources           = null,
            Build             = null,
            Dependencies      = null,
            ManifestPath      = self.ManifestPath,
            IsPreset          = self.IsPreset,
        };

        // preset 顺序合并
        foreach (var p in presets)
        {
            ApplyOverlay(acc, p, sourceFilePath: p.ManifestPath, presetOrigins);
        }
        // self 最终覆盖
        ApplyOverlay(acc, self, sourceFilePath: null, presetOrigins);

        var merged = new MemberManifest
        {
            Project      = acc.Project,
            Sources      = acc.Sources,
            Build        = acc.Build,
            Dependencies = acc.Dependencies,
            Include      = Array.Empty<string>(),  // include 链已展开，merged 不再有 include
            ManifestPath = acc.ManifestPath,
            IsPreset     = acc.IsPreset,
        };

        return new MergeResult(merged, presetOrigins);
    }

    sealed class MergeAccumulator
    {
        public MemberProject Project = MemberProject.Empty();
        public SourcesSection? Sources;
        public BuildSection? Build;
        public IReadOnlyList<MemberDependency>? Dependencies;
        public string ManifestPath = "";
        public bool IsPreset;
    }

    static void ApplyOverlay(
        MergeAccumulator acc,
        MemberManifest overlay,
        string? sourceFilePath,        // null = 这是 self（不记 IncludePreset）
        Dictionary<string, string> origins)
    {
        // [project] 字段级合并
        var p = overlay.Project;
        acc.Project = new MemberProject(
            Name:        p.Name        ?? acc.Project.Name,
            Kind:        p.Kind        ?? acc.Project.Kind,
            Entry:       p.Entry       ?? acc.Project.Entry,
            Pack:        p.Pack        ?? acc.Project.Pack,
            Version:     p.Version     ?? acc.Project.Version,
            Authors:     p.Authors     ?? acc.Project.Authors,
            License:     p.License     ?? acc.Project.License,
            Description: p.Description ?? acc.Project.Description);

        // 记录或清除每个被 overlay 提供的字段的来源
        // sourceFilePath 非 null = preset；null = self（self 覆盖时从 presetOrigins 中清除）
        UpdateOrigin(origins, "[project].kind",        p.Kind        is not null, sourceFilePath);
        UpdateOrigin(origins, "[project].pack",        p.Pack        is not null, sourceFilePath);
        UpdateOrigin(origins, "[project].version",     p.Version     is not null, sourceFilePath);
        UpdateOrigin(origins, "[project].authors",     p.Authors     is not null, sourceFilePath);
        UpdateOrigin(origins, "[project].license",     p.License     is not null, sourceFilePath);
        UpdateOrigin(origins, "[project].description", p.Description is not null, sourceFilePath);

        // [sources]：整体覆盖（数组也整体覆盖；非空赋值即生效）
        if (overlay.Sources is not null)
        {
            acc.Sources = overlay.Sources;
            UpdateOrigin(origins, "[sources]", true, sourceFilePath);
        }

        // [build]
        if (overlay.Build is not null)
        {
            acc.Build = overlay.Build;
            UpdateOrigin(origins, "[build]", true, sourceFilePath);
        }

        // [dependencies]：按 name 字典合并；同 name 后者整体替换
        if (overlay.Dependencies is not null)
        {
            var dict = (acc.Dependencies ?? Array.Empty<MemberDependency>())
                       .ToDictionary(d => d.Name, StringComparer.Ordinal);
            foreach (var d in overlay.Dependencies)
            {
                dict[d.Name] = d;
                UpdateOrigin(origins, $"[dependencies].{d.Name}", true, sourceFilePath);
            }
            acc.Dependencies = dict.Values.ToList();
        }
    }

    /// <summary>
    /// overlay 提供了该字段时：preset 来源记录 sourceFilePath；self 来源（sourceFilePath=null）则清除已记录的 preset 来源。
    /// </summary>
    static void UpdateOrigin(Dictionary<string, string> origins, string fieldPath, bool overlayProvides, string? sourceFilePath)
    {
        if (!overlayProvides) return;
        if (sourceFilePath is null)
        {
            origins.Remove(fieldPath);     // self 覆盖 → 清除 preset 来源记录
        }
        else
        {
            origins[fieldPath] = sourceFilePath;
        }
    }
}
