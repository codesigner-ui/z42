namespace Z42.Project;

/// <summary>
/// 解析 member / preset 的 include 字段，构造 DFS 链路，检测循环 / 深度 / 路径合法性。
///
/// 输出按合并顺序排列的 preset 列表：深度优先展开，每个 include 数组按声明顺序处理。
/// 同一文件被多次拉入（菱形 include）只合并一次（D2.7 决策）。
/// </summary>
public sealed class IncludeResolver
{
    public const int MaxDepth = 8;

    readonly PathTemplateExpander _expander;
    readonly PathTemplateExpander.Context _expanderContext;

    public IncludeResolver(PathTemplateExpander expander, PathTemplateExpander.Context ctx)
    {
        _expander = expander;
        _expanderContext = ctx;
    }

    /// <summary>
    /// 从 member 出发，深度优先展开 include 链。返回应被合并到 base 的 preset 序列
    /// （按合并顺序：先合并的先在前；自身不在内）。
    /// </summary>
    public IReadOnlyList<MemberManifest> Resolve(MemberManifest root)
    {
        var visited  = new HashSet<string>(StringComparer.Ordinal);  // 已合并过的物理路径
        var visiting = new HashSet<string>(StringComparer.Ordinal);  // DFS 当前路径上的节点
        var stack    = new List<string>();                           // 当前栈，用于环报告
        var result   = new List<MemberManifest>();

        // root 自身加入 visiting 防止 self-include
        string rootAbs = Path.GetFullPath(root.ManifestPath);
        visiting.Add(rootAbs);
        stack.Add(rootAbs);

        Visit(root, visited, visiting, stack, result, depth: 0);

        return result;
    }

    void Visit(
        MemberManifest current,
        HashSet<string> visited,
        HashSet<string> visiting,
        List<string> stack,
        List<MemberManifest> output,
        int depth)
    {
        // 当前文件的 include 数组按声明顺序处理
        for (int i = 0; i < current.Include.Count; i++)
        {
            string raw = current.Include[i];
            string fieldPath = $"include[{i}]";

            // 路径合法性 + 模板展开
            string expanded = _expander.Expand(
                raw, _expanderContext, current.ManifestPath, fieldPath, PathTemplateExpander.FieldKind.Path);

            if (HasNetworkScheme(expanded))
                throw Z42Errors.IncludePathNotAllowed(current.ManifestPath, raw, "URLs are not allowed");

            if (ContainsGlobChar(expanded))
                throw Z42Errors.IncludePathNotAllowed(current.ManifestPath, raw, "glob patterns are not allowed in include");

            // 解析为绝对路径（相对 current 文件）
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(current.ManifestPath))!;
            string resolved = Path.IsPathRooted(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(Path.Combine(baseDir, expanded));

            // 仅允许相对/已展开的相对路径；保留前面绝对路径检查（仅当 raw 一开始就是 '/'）
            if (Path.IsPathRooted(raw) && !ContainsTemplateVar(raw))
                throw Z42Errors.IncludePathNotAllowed(current.ManifestPath, raw, "absolute filesystem paths are not allowed");

            if (!File.Exists(resolved))
                throw Z42Errors.IncludePathNotFound(current.ManifestPath, raw, resolved);

            // 菱形：已合并过的跳过
            if (visited.Contains(resolved)) continue;

            // 环检测
            if (visiting.Contains(resolved))
            {
                var cycle = stack.SkipWhile(p => p != resolved).Append(resolved).ToList();
                throw Z42Errors.CircularInclude(cycle);
            }

            // 深度限制
            int newDepth = depth + 1;
            if (newDepth > MaxDepth)
            {
                var chain = stack.Append(resolved).ToList();
                throw Z42Errors.IncludeTooDeep(chain, MaxDepth);
            }

            // 递归
            visiting.Add(resolved);
            stack.Add(resolved);

            var preset = MemberManifest.Load(resolved, isPreset: true);
            Visit(preset, visited, visiting, stack, output, newDepth);

            // preset 自身在所有嵌套的 include 处理完后追加（确保深度优先顺序）
            output.Add(preset);
            visited.Add(resolved);

            stack.RemoveAt(stack.Count - 1);
            visiting.Remove(resolved);
        }
    }

    static bool HasNetworkScheme(string path) =>
        path.Contains("://") &&
        (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("git://", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("file://", StringComparison.OrdinalIgnoreCase));

    static bool ContainsGlobChar(string path) =>
        path.Contains('*') || path.Contains('?') || path.Contains('[');

    static bool ContainsTemplateVar(string path) => path.Contains("${");
}
