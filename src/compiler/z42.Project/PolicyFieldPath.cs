namespace Z42.Project;

/// <summary>
/// 解析 [policy] 段中的字段路径表达式（如 "build.out_dir" / "profile.release.strip"）。
///
/// D3.1 决策：用点分隔的扁平字符串 key，不嵌套 TOML 表。
/// </summary>
public static class PolicyFieldPath
{
    /// <summary>把点路径解析为 token 序列。</summary>
    public static IReadOnlyList<string> Parse(string path)
    {
        if (string.IsNullOrEmpty(path)) return Array.Empty<string>();
        return path.Split('.', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>所有有效的字段路径前缀（用于 WS011 fuzzy 建议）。</summary>
    /// <remarks>
    /// restructure-build-output-dirs (2026-06-06): legacy `build.out_dir`
    /// retired; replaced by the three-field set `build.output_dir` /
    /// `build.cache_dir` / `build.dist_dir`. Policy files that reference
    /// the old path get a fuzzy-match suggestion toward `build.dist_dir`.
    /// </remarks>
    public static readonly IReadOnlyList<string> KnownPaths = new[]
    {
        "build.output_dir",
        "build.cache_dir",
        "build.dist_dir",
        "build.incremental",
        "build.mode",
        "project.kind",
        "project.version",
        "project.license",
        "project.description",
        "project.pack",
        "profile.debug.mode",
        "profile.debug.optimize",
        "profile.debug.debug",
        "profile.debug.strip",
        "profile.debug.pack",
        "profile.release.mode",
        "profile.release.optimize",
        "profile.release.debug",
        "profile.release.strip",
        "profile.release.pack",
    };

    /// <summary>判断字段路径是否被识别。</summary>
    public static bool IsKnown(string path) => KnownPaths.Contains(path, StringComparer.Ordinal);

    /// <summary>
    /// Renamed policy field paths — explicit migration table when the
    /// canonical target is too far from the typo for Levenshtein to
    /// match (e.g. `build.outdir` / `build.out_dir` → `build.dist_dir`,
    /// distance ≈ 4-5). restructure-build-output-dirs (2026-06-06).
    /// </summary>
    static readonly Dictionary<string, string> KnownRenames = new(StringComparer.Ordinal)
    {
        ["build.out_dir"] = "build.dist_dir",
        ["build.outdir"]  = "build.dist_dir",   // common typo + rename in one go
    };

    /// <summary>找编辑距离最近的有效路径，用于错误提示。</summary>
    public static string? FuzzyMatch(string path)
    {
        if (KnownRenames.TryGetValue(path, out var renamed) && KnownPaths.Contains(renamed))
            return renamed;

        string? best = null;
        int bestDist = int.MaxValue;
        foreach (var known in KnownPaths)
        {
            int d = LevenshteinDistance(path, known);
            if (d < bestDist && d <= 3)
            {
                bestDist = d;
                best = known;
            }
        }
        return best;
    }

    /// <summary>读取 ResolvedManifest 中字段路径对应的值（用于 PolicyEnforcer 检查）。</summary>
    public static object? ResolveValue(ResolvedManifest m, string path)
    {
        return path switch
        {
            "build.output_dir"   => m.Build.OutputDir,
            "build.cache_dir"    => m.Build.CacheDir,
            "build.dist_dir"     => m.Build.DistDir,
            "build.incremental"  => m.Build.Incremental,
            "build.mode"         => m.Build.Mode,
            "project.kind"       => m.Kind.ToString().ToLowerInvariant(),
            "project.version"    => m.Version,
            "project.license"    => m.License ?? "",
            "project.description"=> m.Description ?? "",
            "project.pack"       => m.Pack,
            // profile.* 字段在合并后的 ResolvedManifest 中不直接体现（profile 是 CLI 选定的视图），
            // C3 仅做数据查找：profile 字段返回 null 表示"无法在 ResolvedManifest 上验证"
            _                    => null,
        };
    }

    static int LevenshteinDistance(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[n, m];
    }
}
