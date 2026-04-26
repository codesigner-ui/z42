using FluentAssertions;
using Z42.Project;

namespace Z42.Tests;

/// 验证 [workspace.project] / [workspace.dependencies] 共享继承、
/// 旧语法检测、不可共享字段守卫与 member 段限制。
public sealed class WorkspaceInheritanceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "z42_inh_" + Path.GetRandomFileName());

    public WorkspaceInheritanceTests() => Directory.CreateDirectory(_root);
    public void Dispose()             => Directory.Delete(_root, recursive: true);

    string Workspace(string toml)
    {
        string p = Path.Combine(_root, "z42.workspace.toml");
        File.WriteAllText(p, toml);
        return p;
    }

    void Member(string subDir, string fileName, string toml)
    {
        string dir = Path.Combine(_root, subDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), toml);
    }

    // ── workspace.project 字段继承 ─────────────────────────────────────────

    [Fact]
    public void WorkspaceProject_VersionInherited()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [workspace.project]
            version = "0.1.0"
            license = "MIT"
            """);
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            version.workspace = true
            license.workspace = true
            """);

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var rm = loader.LoadWorkspace(ctx).Members[0];

        rm.Version.Should().Be("0.1.0");
        rm.License.Should().Be("MIT");
        rm.Origins["[project].version"].Kind.Should().Be(OriginKind.WorkspaceProject);
        rm.Origins["[project].license"].Kind.Should().Be(OriginKind.WorkspaceProject);
    }

    [Fact]
    public void WorkspaceProject_DirectValueWins()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [workspace.project]
            version = "9.9.9"
            """);
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            version = "0.2.0"
            """);

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var rm = loader.LoadWorkspace(ctx).Members[0];

        rm.Version.Should().Be("0.2.0");
        rm.Origins["[project].version"].Kind.Should().Be(OriginKind.MemberDirect);
    }

    // ── WS032: workspace 字段不存在 ────────────────────────────────────────

    [Fact]
    public void WS032_WorkspaceFieldMissing()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            """);
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            version.workspace = true
            """);

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var act = () => loader.LoadWorkspace(ctx);
        act.Should().Throw<ManifestException>().WithMessage("*WS032*version*");
    }

    // ── WS033: 不可共享字段写在 [workspace.project] ────────────────────────

    [Fact]
    public void WS033_NonShareableFieldInWorkspaceProject()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [workspace.project]
            name = "shared-name"
            """);
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");

        var loader = new ManifestLoader();
        var act = () => loader.DiscoverWorkspaceRoot(_root);
        act.Should().Throw<ManifestException>().WithMessage("*WS033*not be shared*");
    }

    // ── workspace.dependencies 引用 ────────────────────────────────────────

    [Fact]
    public void WorkspaceDependencies_KeyInheritance()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [workspace.dependencies]
            "my-utils" = { version = "1.0.0", path = "libs/utils" }
            """);
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [dependencies]
            "my-utils".workspace = true
            """);

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var rm = loader.LoadWorkspace(ctx).Members[0];

        rm.Dependencies.Should().HaveCount(1);
        var dep = rm.Dependencies[0];
        dep.Name.Should().Be("my-utils");
        dep.Version.Should().Be("1.0.0");
        dep.Path.Should().Be("libs/utils");
        dep.FromWorkspace.Should().BeTrue();
        rm.Origins["[dependencies].my-utils"].Kind.Should().Be(OriginKind.WorkspaceDependency);
    }

    [Fact]
    public void WorkspaceDependencies_TableFormWithLocalOptional()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [workspace.dependencies]
            "lib-a" = "1.0.0"
            """);
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [dependencies]
            "lib-a" = { workspace = true, optional = true }
            """);

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var rm = loader.LoadWorkspace(ctx).Members[0];

        var dep = rm.Dependencies.Single();
        dep.Optional.Should().BeTrue();
        dep.FromWorkspace.Should().BeTrue();
    }

    // ── WS034: workspace 依赖未声明 ────────────────────────────────────────

    [Fact]
    public void WS034_WorkspaceDependencyMissing()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [dependencies]
            "missing".workspace = true
            """);

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var act = () => loader.LoadWorkspace(ctx);
        act.Should().Throw<ManifestException>().WithMessage("*WS034*missing*");
    }

    // ── WS035: 旧语法 version = "workspace" ───────────────────────────────

    [Fact]
    public void WS035_LegacyVersionWorkspaceSyntax()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [dependencies]
            "lib-a" = { version = "workspace" }
            """);

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var act = () => loader.LoadWorkspace(ctx);
        act.Should().Throw<ManifestException>().WithMessage("*WS035*legacy*");
    }

    // ── WS003: member 含禁用段 ─────────────────────────────────────────────

    [Fact]
    public void WS003_MemberHasProfileSection()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [profile.release]
            mode = "jit"
            """);

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var act = () => loader.LoadWorkspace(ctx);
        act.Should().Throw<ManifestException>().WithMessage("*WS003*profile*");
    }

    [Fact]
    public void WS003_MemberHasWorkspaceSection()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [workspace]
            members = []
            """);

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var act = () => loader.LoadWorkspace(ctx);
        act.Should().Throw<ManifestException>().WithMessage("*WS003*workspace*");
    }

    [Fact]
    public void WS003_MemberHasPolicySection()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [policy]
            "build.out_dir" = "x"
            """);

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var act = () => loader.LoadWorkspace(ctx);
        act.Should().Throw<ManifestException>().WithMessage("*WS003*policy*");
    }
}
