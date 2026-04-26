using FluentAssertions;
using Z42.Project;

namespace Z42.Tests;

/// 验证 C3 [policy] 锁定机制 + [workspace.build] 集中产物布局。
public sealed class PolicyAndCentralizedBuildTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "z42_pol_" + Path.GetRandomFileName());

    public PolicyAndCentralizedBuildTests() => Directory.CreateDirectory(_root);
    public void Dispose()                  => Directory.Delete(_root, recursive: true);

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

    ResolvedManifest LoadFoo(string profile = "debug")
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        return loader.LoadWorkspace(ctx, profile).Members.Single(m => m.MemberName == "foo");
    }

    // ── 集中产物布局（默认） ───────────────────────────────────────────────

    [Fact]
    public void CentralizedLayout_DefaultDistAndCache()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");

        var foo = LoadFoo();
        foo.IsCentralized.Should().BeTrue();
        foo.EffectiveOutDir.Should().Be(Path.GetFullPath(Path.Combine(_root, "dist")));
        foo.EffectiveCacheDir.Should().Be(Path.GetFullPath(Path.Combine(_root, ".cache", "foo")));
        foo.EffectiveProductPath.Should().Be(Path.Combine(foo.EffectiveOutDir, "foo.zpkg"));
    }

    [Fact]
    public void CentralizedLayout_ProfileTemplateExpansion()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [workspace.build]
            out_dir = "dist/${profile}"
            """);
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");

        var foo = LoadFoo(profile: "release");
        foo.EffectiveOutDir.Should().Be(Path.GetFullPath(Path.Combine(_root, "dist", "release")));
        foo.EffectiveProductPath.Should().EndWith(Path.Combine("dist", "release", "foo.zpkg"));
    }

    [Fact]
    public void CentralizedLayout_CachePerMemberSubdir()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");
        Member("libs/bar", "bar.z42.toml", "[project]\nname = \"bar\"\nkind = \"lib\"\n");

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var members = loader.LoadWorkspace(ctx).Members;

        members.Single(m => m.MemberName == "foo").EffectiveCacheDir
            .Should().EndWith(Path.Combine(".cache", "foo"));
        members.Single(m => m.MemberName == "bar").EffectiveCacheDir
            .Should().EndWith(Path.Combine(".cache", "bar"));
    }

    [Fact]
    public void StandaloneMode_NotCentralized()
    {
        // 单工程模式（无 workspace 根）
        string singleDir = Path.Combine(_root, "lonely");
        Directory.CreateDirectory(singleDir);
        File.WriteAllText(Path.Combine(singleDir, "foo.z42.toml"), "[project]\nname = \"foo\"\nkind = \"lib\"\n");

        var loader = new ManifestLoader();
        var rm = loader.LoadStandalone(Path.Combine(singleDir, "foo.z42.toml"));
        rm.IsCentralized.Should().BeFalse();
        rm.EffectiveOutDir.Should().EndWith("dist");
    }

    // ── 默认锁定字段（D5）─────────────────────────────────────────────────

    [Fact]
    public void DefaultLock_BuildOutDir_MemberOverride_WS010()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");  // 默认锁定 build.out_dir
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [build]
            out_dir = "custom_dist"
            """);

        var act = () => LoadFoo();
        act.Should().Throw<ManifestException>().WithMessage("*WS010*build.out_dir*custom_dist*");
    }

    [Fact]
    public void DefaultLock_MemberMatchesWorkspace_NoConflict()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [build]
            out_dir = "dist"
            """);

        var foo = LoadFoo();
        foo.Origins["build.out_dir"].Kind.Should().Be(OriginKind.PolicyLocked);
    }

    // ── 显式 policy 字段 ──────────────────────────────────────────────────

    [Fact]
    public void ExplicitPolicy_LockBuildMode_WS010()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [policy]
            "build.mode" = "interp"
            """);
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [build]
            mode = "jit"
            """);

        var act = () => LoadFoo();
        act.Should().Throw<ManifestException>().WithMessage("*WS010*build.mode*jit*");
    }

    // ── WS011: 字段路径不存在 ─────────────────────────────────────────────

    [Fact]
    public void WS011_UnknownPolicyField()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [policy]
            "build.unknown_field" = "value"
            """);
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");

        var act = () => LoadFoo();
        act.Should().Throw<ManifestException>().WithMessage("*WS011*build.unknown_field*");
    }

    [Fact]
    public void WS011_FuzzyMatchSuggestion()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [policy]
            "build.outdir" = "dist"
            """);
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");

        var act = () => LoadFoo();
        act.Should().Throw<ManifestException>().WithMessage("*WS011*outdir*build.out_dir*");
    }

    // ── PolicyLocked Origin 标注 ──────────────────────────────────────────

    [Fact]
    public void PolicyLocked_OriginInResolvedManifest()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            """);
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [build]
            out_dir = "dist"
            """);

        var foo = LoadFoo();
        foo.Origins["build.out_dir"].Kind.Should().Be(OriginKind.PolicyLocked);
        foo.Origins["build.out_dir"].FilePath.Should().EndWith("z42.workspace.toml");
    }
}
