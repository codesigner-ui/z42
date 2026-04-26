namespace Z42.Project;

/// <summary>
/// 应用 workspace [policy] 段：检测 member / preset 是否违反锁定字段。
///
/// 默认锁定字段集合（D3.2）：build.out_dir / build.cache_dir。
/// 用户可在 [policy] 段显式声明更多锁定字段。
///
/// 检测时机：member ResolveMember() 完成后，针对最终 ResolvedManifest 比对锁定值。
/// </summary>
public sealed class PolicyEnforcer
{
    /// <summary>D3.2 默认锁定字段。即使 [policy] 段为空也自动锁定。</summary>
    public static readonly IReadOnlyList<string> DefaultLockedPaths = new[]
    {
        "build.out_dir",
        "build.cache_dir",
    };

    /// <summary>
    /// 应用 workspace policy 到 member ResolvedManifest。返回更新了 Origins
    /// (PolicyLocked 标注) 的 manifest；如果违规则抛 WS010 / WS011。
    /// </summary>
    public ResolvedManifest Enforce(
        ResolvedManifest member,
        WorkspaceManifest? workspace,
        IReadOnlyDictionary<string, FieldOrigin> baseOrigins)
    {
        if (workspace is null) return member;  // 单工程模式无 policy

        // 计算最终锁定字典：默认 ∪ 显式 [policy]
        var locked = new Dictionary<string, object?>(StringComparer.Ordinal);

        // 默认锁定：值取自 [workspace.build]
        if (DefaultLockedPaths.Contains("build.out_dir"))
            locked["build.out_dir"] = workspace.WorkspaceBuild.OutDir;
        if (DefaultLockedPaths.Contains("build.cache_dir"))
            locked["build.cache_dir"] = workspace.WorkspaceBuild.CacheDir;

        // 显式 [policy] 段
        foreach (var kv in workspace.Policy)
        {
            // WS011: 字段路径不存在
            if (!PolicyFieldPath.IsKnown(kv.Key))
            {
                throw Z42Errors.PolicyFieldPathNotFound(kv.Key, PolicyFieldPath.FuzzyMatch(kv.Key));
            }
            locked[kv.Key] = NormalizeValue(kv.Value);
        }

        // 检查 member 当前值是否冲突。仅检查 member 显式声明（来源 = MemberDirect / IncludePreset）的字段；
        // 未显式声明的字段（如 member 没写 [build]）由 workspace 锁定值替代，不冲突。
        var origins = new Dictionary<string, FieldOrigin>(baseOrigins, StringComparer.Ordinal);
        foreach (var (path, expected) in locked)
        {
            // 仅当 member 显式声明（origin 存在且来自 member/preset）才检查冲突
            if (!baseOrigins.TryGetValue(path, out var origin)) continue;
            if (origin.Kind != OriginKind.MemberDirect && origin.Kind != OriginKind.IncludePreset) continue;

            object? actual = PolicyFieldPath.ResolveValue(member, path);
            if (actual is null) continue;

            if (!ValuesEqual(expected, actual))
            {
                throw Z42Errors.PolicyViolation(
                    fieldPath:       path,
                    lockedValue:     expected,
                    memberValue:     actual,
                    lockedFromFile:  workspace.ManifestPath,
                    memberFromFile:  origin.FilePath);
            }

            // 一致 → 标记 PolicyLocked（来源含 workspace policy）
            origins[path] = new FieldOrigin(workspace.ManifestPath, path, OriginKind.PolicyLocked);
        }

        return member with { Origins = origins };
    }

    /// <summary>把 TomlObject 标量统一转为 .NET 原生类型用于比较。</summary>
    static object? NormalizeValue(object? raw)
    {
        return raw switch
        {
            null     => null,
            string s => s,
            bool b   => b,
            long l   => l,
            int i    => (long)i,
            _        => raw,
        };
    }

    static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        // string vs string
        if (a is string sa && b is string sb) return sa == sb;
        // bool vs bool
        if (a is bool ba && b is bool bb) return ba == bb;
        // 数字
        if (a is long la && b is long lb) return la == lb;
        if (a is long la2 && b is int ib) return la2 == ib;
        if (a is int ia && b is long lb2) return ia == lb2;
        return a.Equals(b);
    }
}
