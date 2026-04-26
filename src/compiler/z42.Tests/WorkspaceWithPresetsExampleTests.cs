using FluentAssertions;
using Z42.Project;

namespace Z42.Tests;

/// 端到端：examples/workspace-with-presets/ 集成验证 C1 + C2。
public sealed class WorkspaceWithPresetsExampleTests
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
                {
                    return Path.Combine(dir.FullName, "examples", "workspace-with-presets");
                }
                dir = dir.Parent;
            }
            throw new InvalidOperationException("repo root not found");
        }
    }

    [Fact]
    public void Foo_InheritsFromLibDefaults()
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(ExampleRoot)!;
        var foo = loader.LoadWorkspace(ctx).Members.Single(m => m.MemberName == "foo");

        foo.Kind.Should().Be(ProjectKind.Lib);
        foo.Origins["[project].kind"].Kind.Should().Be(OriginKind.IncludePreset);
        foo.Origins["[project].kind"].FilePath.Should().EndWith("lib-defaults.toml");
        foo.Sources.Include.Should().Contain("src/**/*.z42");
        foo.Sources.Exclude.Should().Contain("src/**/*_test.z42");
    }

    [Fact]
    public void Bar_StrictLintsOverridesLibDefaultsBuildMode()
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(ExampleRoot)!;
        var bar = loader.LoadWorkspace(ctx).Members.Single(m => m.MemberName == "bar");

        bar.Kind.Should().Be(ProjectKind.Lib);   // 来自 lib-defaults
        bar.Build.Mode.Should().Be("interp");    // 来自 strict-lints（覆盖 lib-defaults 中没设的 mode）
    }

    [Fact]
    public void Foo_VersionFromWorkspace_LicenseFromWorkspace()
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(ExampleRoot)!;
        var foo = loader.LoadWorkspace(ctx).Members.Single(m => m.MemberName == "foo");

        foo.Version.Should().Be("0.1.0");
        foo.License.Should().Be("MIT");
        foo.Origins["[project].version"].Kind.Should().Be(OriginKind.WorkspaceProject);
        foo.Origins["[project].license"].Kind.Should().Be(OriginKind.WorkspaceProject);
    }
}
