using FluentAssertions;
using Z42.Project;

namespace Z42.Tests;

/// 端到端验证 examples/workspace-basic/ 能被 ManifestLoader 正确解析。
/// 这是 C1 的"集成 golden test"，覆盖 workspace.project 继承 +
/// workspace.dependencies 引用 + members glob + default-members。
public sealed class WorkspaceBasicExampleTests
{
    static string ExampleRoot
    {
        get
        {
            // tests 二进制运行在 artifacts/compiler/z42.Tests/bin/
            // 向上找到 repo root，再拼 examples/workspace-basic
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "examples")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "src", "compiler")))
                {
                    return Path.Combine(dir.FullName, "examples", "workspace-basic");
                }
                dir = dir.Parent;
            }
            throw new InvalidOperationException("repo root not found");
        }
    }

    [Fact]
    public void Discover_FindsWorkspaceRoot()
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(ExampleRoot);
        ctx.Should().NotBeNull();
        ctx!.Manifest.MembersPatterns.Should().BeEquivalentTo(new[] { "libs/*", "apps/*" });
        ctx.Manifest.DefaultMembers.Should().BeEquivalentTo(new[] { "apps/hello" });
        ctx.Manifest.WorkspaceProject.Should().NotBeNull();
        ctx.Manifest.WorkspaceProject!.Version.Should().Be("0.1.0");
        ctx.Manifest.WorkspaceProject.License.Should().Be("MIT");
    }

    [Fact]
    public void LoadWorkspace_ResolvesAllMembers()
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(ExampleRoot)!;
        var result = loader.LoadWorkspace(ctx);

        result.Members.Should().HaveCount(2);
        result.Members.Select(m => m.MemberName).Should().BeEquivalentTo(new[] { "greeter", "hello" });
        result.Warnings.Should().BeEmpty("no orphan members in workspace-basic");
    }

    [Fact]
    public void Greeter_InheritsVersionFromWorkspace()
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(ExampleRoot)!;
        var greeter = loader.LoadWorkspace(ctx).Members.Single(m => m.MemberName == "greeter");

        greeter.Version.Should().Be("0.1.0");
        greeter.License.Should().Be("MIT");
        greeter.Authors.Should().BeEquivalentTo(new[] { "z42 team" });
        greeter.Origins["[project].version"].Kind.Should().Be(OriginKind.WorkspaceProject);
        greeter.Kind.Should().Be(ProjectKind.Lib);
    }

    [Fact]
    public void Hello_HasGreeterDependencyFromWorkspace()
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(ExampleRoot)!;
        var hello = loader.LoadWorkspace(ctx).Members.Single(m => m.MemberName == "hello");

        hello.Kind.Should().Be(ProjectKind.Exe);
        hello.Entry.Should().Be("Hello.main");
        hello.Dependencies.Should().HaveCount(1);
        var greeterDep = hello.Dependencies[0];
        greeterDep.Name.Should().Be("greeter");
        greeterDep.Version.Should().Be("0.1.0");
        greeterDep.Path.Should().Be("libs/greeter");
        greeterDep.FromWorkspace.Should().BeTrue();
    }
}
