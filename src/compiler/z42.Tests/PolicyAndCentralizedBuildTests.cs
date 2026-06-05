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
        // restructure-build-output-dirs (2026-06-06): default cascade —
        // output_dir = workspace_root, dist = ${output_dir}/dist,
        // cache = ${output_dir}/.cache (+ member subdir anti-collision).
        foo.EffectiveOutputDir.Should().Be(Path.GetFullPath(_root));
        foo.EffectiveDistDir.Should().Be(Path.GetFullPath(Path.Combine(_root, "dist")));
        foo.EffectiveCacheDir.Should().Be(Path.GetFullPath(Path.Combine(_root, ".cache", "foo")));
        foo.EffectiveProductPath.Should().Be(Path.Combine(foo.EffectiveDistDir, "foo.zpkg"));
    }

    [Fact]
    public void CentralizedLayout_ProfileTemplateExpansion()
    {
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [workspace.build]
            dist_dir = "dist/${profile}"
            """);
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");

        var foo = LoadFoo(profile: "release");
        foo.EffectiveDistDir.Should().Be(Path.GetFullPath(Path.Combine(_root, "dist", "release")));
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
        // restructure-build-output-dirs (2026-06-06): single-project mode
        // also populates EffectiveDistDir (renamed from EffectiveOutDir).
        rm.EffectiveDistDir.Should().EndWith("dist");
    }

    // ── 默认锁定字段（D5）─────────────────────────────────────────────────

    [Fact]
    public void DefaultLock_BuildDistDir_MemberOverride_WS010()
    {
        // restructure-build-output-dirs (2026-06-06): default-locked path
        // renamed `build.out_dir` → `build.dist_dir`; workspace defaults
        // to null (unset = cascade) so any explicit member override is a
        // conflict.
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [build]
            dist_dir = "custom_dist"
            """);

        var act = () => LoadFoo();
        act.Should().Throw<ManifestException>().WithMessage("*WS010*build.dist_dir*custom_dist*");
    }

    [Fact]
    public void DefaultLock_MemberMatchesWorkspace_NoConflict()
    {
        // Both workspace and member set the same dist_dir → no conflict;
        // origin promoted to PolicyLocked.
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [workspace.build]
            dist_dir = "dist"
            """);
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [build]
            dist_dir = "dist"
            """);

        var foo = LoadFoo();
        foo.Origins["build.dist_dir"].Kind.Should().Be(OriginKind.PolicyLocked);
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
        // restructure-build-output-dirs (2026-06-06): nearest known field
        // for typo `outdir` is now `build.dist_dir` (renamed from
        // `out_dir`); Levenshtein distance ≤ 3.
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [policy]
            "build.outdir" = "dist"
            """);
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");

        var act = () => LoadFoo();
        act.Should().Throw<ManifestException>().WithMessage("*WS011*outdir*build.dist_dir*");
    }

    // ── PolicyLocked Origin 标注 ──────────────────────────────────────────

    [Fact]
    public void PolicyLocked_OriginInResolvedManifest()
    {
        // restructure-build-output-dirs (2026-06-06): need to mirror
        // workspace + member on `dist_dir` so the policy match promotes
        // member's MemberDirect origin to PolicyLocked.
        Workspace("""
            [workspace]
            members = ["libs/*"]
            [workspace.build]
            dist_dir = "dist"
            """);
        Member("libs/foo", "foo.z42.toml", """
            [project]
            name = "foo"
            kind = "lib"
            [build]
            dist_dir = "dist"
            """);

        var foo = LoadFoo();
        foo.Origins["build.dist_dir"].Kind.Should().Be(OriginKind.PolicyLocked);
        foo.Origins["build.dist_dir"].FilePath.Should().EndWith("z42.workspace.toml");
    }
}
