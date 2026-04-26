using FluentAssertions;
using Z42.Pipeline;
using Z42.Project;

namespace Z42.Tests;

/// 验证 MemberDependencyGraph：闭包 / 拓扑层 / 三色 DFS 环检测。
public sealed class MemberDependencyGraphTests
{
    static ResolvedManifest M(string name, params string[] deps)
    {
        var resolvedDeps = deps.Select(d => new ResolvedDependency(d, "*", null, false, false)).ToList();
        return new ResolvedManifest(
            MemberName:    name,
            Kind:          ProjectKind.Lib,
            Entry:         null,
            Version:       "0.1.0",
            Authors:       Array.Empty<string>(),
            License:       null,
            Description:   null,
            Pack:          null,
            Sources:       new SourcesSection(["src/**/*.z42"], []),
            Build:         new BuildSection("dist", "interp", true),
            Dependencies:  resolvedDeps,
            Origins:       new Dictionary<string, FieldOrigin>(),
            ManifestPath:  $"/tmp/{name}.z42.toml",
            WorkspaceRoot: "/tmp");
    }

    // ── 闭包 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Closure_LinearChain()
    {
        var g = new MemberDependencyGraph([M("a"), M("b", "a"), M("c", "b")]);
        g.Closure(["c"]).Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }

    [Fact]
    public void Closure_DiamondDeduped()
    {
        var g = new MemberDependencyGraph([M("a"), M("b", "a"), M("c", "a"), M("d", "b", "c")]);
        g.Closure(["d"]).Should().BeEquivalentTo(new[] { "a", "b", "c", "d" });
    }

    [Fact]
    public void Closure_OnlyWorkspaceMembers()
    {
        // 外部依赖不应进入 graph
        var g = new MemberDependencyGraph([M("a", "external-lib")]);
        g.DirectDependencies("a").Should().BeEmpty();
    }

    // ── 拓扑层 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Layers_Linear()
    {
        var g = new MemberDependencyGraph([M("a"), M("b", "a"), M("c", "b")]);
        var layers = g.TopologicalLayers();
        layers.Should().HaveCount(3);
        layers[0].Should().BeEquivalentTo(new[] { "a" });
        layers[1].Should().BeEquivalentTo(new[] { "b" });
        layers[2].Should().BeEquivalentTo(new[] { "c" });
    }

    [Fact]
    public void Layers_DiamondGroupsParallelMembers()
    {
        // a → b, c; b → d, c → d  ⇒ 层 [a] [b,c] [d]
        var g = new MemberDependencyGraph([M("a"), M("b", "a"), M("c", "a"), M("d", "b", "c")]);
        var layers = g.TopologicalLayers();
        layers.Should().HaveCount(3);
        layers[0].Should().BeEquivalentTo(new[] { "a" });
        layers[1].Should().BeEquivalentTo(new[] { "b", "c" });
        layers[2].Should().BeEquivalentTo(new[] { "d" });
    }

    [Fact]
    public void Layers_Subset()
    {
        // 全图 [a, b, c, d, e]，子集只编 d 应返回闭包内的层
        var g = new MemberDependencyGraph([M("a"), M("b", "a"), M("c", "b"), M("d", "c"), M("e")]);
        var subset = g.Closure(["d"]);
        var layers = g.TopologicalLayers(subset);
        layers.SelectMany(l => l).Should().BeEquivalentTo(new[] { "a", "b", "c", "d" });
        layers.SelectMany(l => l).Should().NotContain("e");
    }

    // ── 环检测 ───────────────────────────────────────────────────────────────

    [Fact]
    public void FindCycle_NoCycle_ReturnsNull()
    {
        var g = new MemberDependencyGraph([M("a"), M("b", "a"), M("c", "b")]);
        g.FindCycle().Should().BeNull();
    }

    [Fact]
    public void FindCycle_SelfLoop()
    {
        var g = new MemberDependencyGraph([M("a", "a")]);
        var cycle = g.FindCycle();
        cycle.Should().NotBeNull();
        cycle!.Should().BeEquivalentTo(new[] { "a", "a" });
    }

    [Fact]
    public void FindCycle_TwoNodeCycle()
    {
        var g = new MemberDependencyGraph([M("a", "b"), M("b", "a")]);
        var cycle = g.FindCycle();
        cycle.Should().NotBeNull();
        cycle!.Should().Contain(new[] { "a", "b" });
    }

    [Fact]
    public void FindCycle_IndirectCycle()
    {
        var g = new MemberDependencyGraph([M("a", "b"), M("b", "c"), M("c", "a")]);
        var cycle = g.FindCycle();
        cycle.Should().NotBeNull();
        cycle!.Distinct().Should().HaveCount(3);
    }
}
