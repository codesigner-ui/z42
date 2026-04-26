using Z42.Project;

namespace Z42.Pipeline;

/// <summary>
/// 跨 member 依赖图。基于 ResolvedManifest.Dependencies 构造，DFS 三色检测环（WS006），
/// 输出拓扑层（同层 member 互不依赖，可以并行；C4a 串行遍历）。
/// </summary>
public sealed class MemberDependencyGraph
{
    readonly Dictionary<string, List<string>> _edges;       // member name → list of dep names
    readonly Dictionary<string, ResolvedManifest> _byName;

    public MemberDependencyGraph(IReadOnlyList<ResolvedManifest> members)
    {
        _byName = members.ToDictionary(m => m.MemberName, StringComparer.Ordinal);
        _edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var m in members)
        {
            var deps = m.Dependencies
                .Where(d => _byName.ContainsKey(d.Name))   // 仅 workspace 内 member 之间的边
                .Select(d => d.Name)
                .ToList();
            _edges[m.MemberName] = deps;
        }
    }

    /// <summary>所有 member 名（按词典序）。</summary>
    public IEnumerable<string> AllMembers() => _byName.Keys.OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>获取 member 的直接依赖。</summary>
    public IReadOnlyList<string> DirectDependencies(string memberName) =>
        _edges.TryGetValue(memberName, out var list) ? list : Array.Empty<string>();

    /// <summary>计算闭包：targets ∪ 它们的所有传递依赖。</summary>
    public IReadOnlyList<string> Closure(IEnumerable<string> targets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(targets);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!seen.Add(n)) continue;
            foreach (var dep in DirectDependencies(n))
                stack.Push(dep);
        }
        return seen.ToList();
    }

    /// <summary>找到第一个环；返回环路径（含起点），无环返回 null。</summary>
    public IReadOnlyList<string>? FindCycle()
    {
        var color = new Dictionary<string, int>(StringComparer.Ordinal);   // 0=white 1=gray 2=black
        var pathStack = new List<string>();

        foreach (var node in _edges.Keys)
        {
            if (!color.ContainsKey(node) || color[node] == 0)
            {
                var cycle = DfsFindCycle(node, color, pathStack);
                if (cycle is not null) return cycle;
            }
        }
        return null;
    }

    IReadOnlyList<string>? DfsFindCycle(string node, Dictionary<string, int> color, List<string> pathStack)
    {
        color[node] = 1;
        pathStack.Add(node);

        foreach (var next in DirectDependencies(node))
        {
            if (!color.TryGetValue(next, out var c) || c == 0)
            {
                var cycle = DfsFindCycle(next, color, pathStack);
                if (cycle is not null) return cycle;
            }
            else if (c == 1)
            {
                // 找到环：从 pathStack 中 next 起到当前节点
                int idx = pathStack.IndexOf(next);
                var cycle = pathStack.Skip(idx).Append(next).ToList();
                return cycle;
            }
        }

        color[node] = 2;
        pathStack.RemoveAt(pathStack.Count - 1);
        return null;
    }

    /// <summary>
    /// 计算拓扑层。每层互相独立可并行；返回层列表（前层在前，后层在后）。
    /// 调用前应先 FindCycle 检查无环；含环时行为未定义。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> TopologicalLayers(IEnumerable<string>? subset = null)
    {
        var nodes = new HashSet<string>(subset ?? _edges.Keys, StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var reverseEdges = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var n in nodes) { inDegree[n] = 0; reverseEdges[n] = new List<string>(); }
        foreach (var n in nodes)
        {
            foreach (var dep in DirectDependencies(n).Where(nodes.Contains))
            {
                inDegree[n]++;                                  // n depends on dep → dep must come first
                reverseEdges[dep].Add(n);                       // dep → n（反向，便于 layer 推进）
            }
        }

        var layers = new List<IReadOnlyList<string>>();
        var current = nodes.Where(n => inDegree[n] == 0).OrderBy(n => n, StringComparer.Ordinal).ToList();

        while (current.Count > 0)
        {
            layers.Add(current);
            var next = new List<string>();
            foreach (var n in current)
            {
                foreach (var follower in reverseEdges[n])
                {
                    if (--inDegree[follower] == 0)
                        next.Add(follower);
                }
            }
            current = next.OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        return layers;
    }

    /// <summary>所有依赖图的边（用于 metadata / tree 命令）。</summary>
    public IEnumerable<(string From, string To)> Edges()
    {
        foreach (var (from, deps) in _edges)
            foreach (var to in deps)
                yield return (from, to);
    }
}
