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
    public static readonly IReadOnlyList<string> KnownPaths = new[]
    {
        "build.out_dir",
        "build.cache_dir",
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

    /// <summary>找编辑距离最近的有效路径，用于错误提示。</summary>
    public static string? FuzzyMatch(string path)
    {
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
            "build.out_dir"      => m.Build.OutDir,
            "build.cache_dir"    => null,                    // C1 BuildSection 暂无 cache_dir 字段
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
