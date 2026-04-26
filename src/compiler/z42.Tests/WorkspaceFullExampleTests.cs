using FluentAssertions;
using Z42.Pipeline;
using Z42.Project;

namespace Z42.Tests;

/// 端到端验证 examples/workspace-full/ 的 manifest + orchestrator 集成（不实际编译源码）。
public sealed class WorkspaceFullExampleTests
{
    static string ExampleRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "examples")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "src", "compiler")))
                    return Path.Combine(dir.FullName, "examples", "workspace-full");
                dir = dir.Parent;
            }
            throw new InvalidOperationException("repo root not found");
        }
    }

    [Fact]
    public void DependencyGraph_HelloDependsOnUtilsDependsOnCore()
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(ExampleRoot)!;
        var result = loader.LoadWorkspace(ctx);

        result.Members.Should().HaveCount(3);
        var graph = new MemberDependencyGraph(result.Members);

        graph.DirectDependencies("hello").Should().BeEquivalentTo(new[] { "utils" });
        graph.DirectDependencies("utils").Should().BeEquivalentTo(new[] { "core" });
        graph.DirectDependencies("core").Should().BeEmpty();

        graph.FindCycle().Should().BeNull();
    }

    [Fact]
    public void Orchestrator_TopologicalOrder()
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(ExampleRoot)!;
        var result = loader.LoadWorkspace(ctx);

        var order = new List<string>();
        var orch = new WorkspaceBuildOrchestrator
        {
            CompileMember = (m, _) => { order.Add(m.MemberName); return 0; }
        };

        var report = orch.Build(
            result,
            ctx.Manifest.DefaultMembers,
            new WorkspaceBuildOrchestrator.BuildOptions(
                Selected:     Array.Empty<string>(),
                Excluded:     Array.Empty<string>(),
                AllWorkspace: true,
                CheckOnly:    true,
                Release:      false));

        // 拓扑顺序：core 必须在 utils 之前，utils 必须在 hello 之前
        order.IndexOf("core").Should().BeLessThan(order.IndexOf("utils"));
        order.IndexOf("utils").Should().BeLessThan(order.IndexOf("hello"));
        report.AllSucceeded.Should().BeTrue();
    }

    [Fact]
    public void Orchestrator_DefaultMembers_OnlyHelloAndDeps()
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(ExampleRoot)!;
        var result = loader.LoadWorkspace(ctx);

        var compiled = new HashSet<string>();
        var orch = new WorkspaceBuildOrchestrator
        {
            CompileMember = (m, _) => { compiled.Add(m.MemberName); return 0; }
        };

        // default-members = ["apps/hello"]，应该编译 hello + 其闭包
        orch.Build(result, ctx.Manifest.DefaultMembers,
            new WorkspaceBuildOrchestrator.BuildOptions(
                Selected:     Array.Empty<string>(),
                Excluded:     Array.Empty<string>(),
                AllWorkspace: false,
                CheckOnly:    true,
                Release:      false));

        compiled.Should().BeEquivalentTo(new[] { "core", "utils", "hello" });
    }
}
