using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Z42.Project;

/// <summary>
/// 把 [workspace] members glob 模式展开为具体目录列表。
///
/// 仅匹配目录（不匹配文件）；目录下必须恰好一份 *.z42.toml 才算合法 member。
/// 配合 exclude 排除子集；结果按词典序去重。
/// </summary>
public sealed class GlobExpander
{
    /// <summary>
    /// 单个展开结果。
    /// </summary>
    public sealed record MemberLocation(
        string DirectoryPath,
        string ManifestPath);

    /// <summary>
    /// 展开 members + exclude，返回去重后的 member 列表。
    /// 同一目录两份 *.z42.toml → WS005。
    /// </summary>
    public IReadOnlyList<MemberLocation> Expand(
        string                      rootDir,
        IReadOnlyList<string>       memberPatterns,
        IReadOnlyList<string>       excludePatterns)
    {
        var matched = new SortedDictionary<string, MemberLocation>(StringComparer.Ordinal);

        foreach (string pattern in memberPatterns)
        {
            // glob 模式 vs 显式路径
            if (ContainsGlobChars(pattern))
            {
                foreach (var dir in EnumerateDirectoryGlob(rootDir, pattern))
                {
                    string? manifest = FindMemberManifest(dir);
                    if (manifest is null) continue;          // 跳过无 manifest 的目录
                    matched[Path.GetFullPath(dir)] = new MemberLocation(dir, manifest);
                }
            }
            else
            {
                string fullDir = Path.IsPathRooted(pattern)
                    ? pattern
                    : Path.Combine(rootDir, pattern);
                if (!Directory.Exists(fullDir)) continue;

                string? manifest = FindMemberManifest(fullDir);
                if (manifest is null) continue;
                matched[Path.GetFullPath(fullDir)] = new MemberLocation(fullDir, manifest);
            }
        }

        // 应用 exclude（基于绝对路径前缀）
        if (excludePatterns.Count > 0)
        {
            var excluded = new HashSet<string>(StringComparer.Ordinal);
            foreach (string pattern in excludePatterns)
            {
                if (ContainsGlobChars(pattern))
                {
                    foreach (var dir in EnumerateDirectoryGlob(rootDir, pattern))
                        excluded.Add(Path.GetFullPath(dir));
                }
                else
                {
                    string fullDir = Path.IsPathRooted(pattern)
                        ? pattern
                        : Path.Combine(rootDir, pattern);
                    excluded.Add(Path.GetFullPath(fullDir));
                }
            }
            foreach (var key in matched.Keys.ToList())
            {
                if (excluded.Contains(key)) matched.Remove(key);
            }
        }

        return matched.Values.ToList();
    }

    static bool ContainsGlobChars(string pattern) =>
        pattern.Contains('*') || pattern.Contains('?') || pattern.Contains('[');

    /// <summary>枚举匹配 glob 的目录。复用 FileSystemGlobbing 但只取目录路径。</summary>
    static IEnumerable<string> EnumerateDirectoryGlob(string rootDir, string pattern)
    {
        // 使 pattern 匹配 "<dir>/" 形式：glob 库匹配文件，加 "/*.z42.toml" 后取所在目录
        var matcher = new Matcher(StringComparison.Ordinal);
        // 让 pattern 后跟 /*.z42.toml 限定为含 manifest 的目录
        matcher.AddInclude(pattern.TrimEnd('/') + "/*.z42.toml");

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(rootDir)));
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in result.Files)
        {
            string filePath = Path.Combine(rootDir, f.Path);
            string? dir = Path.GetDirectoryName(filePath);
            if (dir is not null) dirs.Add(dir);
        }
        return dirs;
    }

    /// <summary>找目录下唯一 *.z42.toml；多份 → WS005；零份 → null。</summary>
    static string? FindMemberManifest(string dir)
    {
        var files = Directory.GetFiles(dir, "*.z42.toml")
                             .Where(f => !Path.GetFileName(f).Equals("z42.workspace.toml", StringComparison.Ordinal))
                             .ToArray();
        return files.Length switch
        {
            0 => null,
            1 => files[0],
            _ => throw Z42Errors.AmbiguousManifest(dir, files),
        };
    }
}
