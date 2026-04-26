using FluentAssertions;
using Z42.Project;

namespace Z42.Tests;

/// 验证 ManifestLoader 的 workspace 发现、virtual manifest、members 展开、
/// orphan 检测、字段继承与 member 段限制。
public sealed class WorkspaceManifestTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "z42_ws_" + Path.GetRandomFileName());

    public WorkspaceManifestTests() => Directory.CreateDirectory(_root);
    public void Dispose()           => Directory.Delete(_root, recursive: true);

    // ── Helpers ──────────────────────────────────────────────────────────────

    string Workspace(string toml)
    {
        string p = Path.Combine(_root, "z42.workspace.toml");
        File.WriteAllText(p, toml);
        return p;
    }

    string Member(string subDir, string fileName, string toml)
    {
        string dir = Path.Combine(_root, subDir);
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, fileName);
        File.WriteAllText(p, toml);
        return p;
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    [Fact]
    public void Discover_FromRoot_FindsWorkspace()
    {
        Workspace("[workspace]\nmembers = []\n");
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root);
        ctx.Should().NotBeNull();
        ctx!.Manifest.RootDirectory.Should().Be(Path.GetFullPath(_root));
    }

    [Fact]
    public void Discover_FromSubdirectory_WalksUp()
    {
        Workspace("[workspace]\nmembers = []\n");
        string subDir = Path.Combine(_root, "libs", "deep", "nested");
        Directory.CreateDirectory(subDir);

        var ctx = new ManifestLoader().DiscoverWorkspaceRoot(subDir);
        ctx.Should().NotBeNull();
    }

    [Fact]
    public void Discover_NoWorkspace_ReturnsNull()
    {
        // _root 内不放 z42.workspace.toml；走到 /tmp 也没有
        var ctx = new ManifestLoader().DiscoverWorkspaceRoot(_root);
        ctx.Should().BeNull();
    }

    // ── WS030: 文件名校验 ──────────────────────────────────────────────────────

    [Fact]
    public void WS030_WorkspaceSectionInWrongFileName()
    {
        // 把 [workspace] 段放在 "wrong.z42.toml" 里
        string p = Member("", "wrong.z42.toml", "[workspace]\nmembers = []\n");
        var act = () => WorkspaceManifest.Load(p);
        act.Should().Throw<ManifestException>().WithMessage("*WS030*z42.workspace.toml*");
    }

    // ── WS036: virtual manifest ──────────────────────────────────────────────

    [Fact]
    public void WS036_RootHasProjectSection()
    {
        Workspace("[workspace]\nmembers = []\n[project]\nname = \"hello\"\n");
        var act = () => WorkspaceManifest.Load(Path.Combine(_root, "z42.workspace.toml"));
        act.Should().Throw<ManifestException>().WithMessage("*WS036*virtual*");
    }

    // ── Members glob 展开 ─────────────────────────────────────────────────────

    [Fact]
    public void Members_GlobExpansion_FindsMembers()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");
        Member("libs/bar", "bar.z42.toml", "[project]\nname = \"bar\"\nkind = \"lib\"\n");

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var result = loader.LoadWorkspace(ctx);

        result.Members.Should().HaveCount(2);
        result.Members.Select(m => m.MemberName).Should().BeEquivalentTo(new[] { "foo", "bar" });
    }

    [Fact]
    public void Members_ExcludeFiltersOut()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\nexclude = [\"libs/sandbox\"]\n");
        Member("libs/foo", "foo.z42.toml",     "[project]\nname = \"foo\"\nkind = \"lib\"\n");
        Member("libs/sandbox", "x.z42.toml",   "[project]\nname = \"sandbox\"\nkind = \"lib\"\n");

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var result = loader.LoadWorkspace(ctx);

        result.Members.Should().HaveCount(1);
        result.Members[0].MemberName.Should().Be("foo");
    }

    // ── WS005: 同目录两份 manifest ────────────────────────────────────────────

    [Fact]
    public void WS005_TwoManifestsInSameDir()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");
        Member("libs/foo", "bar.z42.toml", "[project]\nname = \"bar\"\nkind = \"lib\"\n");

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var act = () => loader.LoadWorkspace(ctx);
        act.Should().Throw<ManifestException>().WithMessage("*WS005*");
    }

    // ── WS031: default-members 校验 ───────────────────────────────────────────

    [Fact]
    public void WS031_DefaultMembersUnknown()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\ndefault-members = [\"apps/missing\"]\n");
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var act = () => loader.LoadWorkspace(ctx);
        act.Should().Throw<ManifestException>().WithMessage("*WS031*apps/missing*");
    }

    // ── WS007: orphan member（warning）────────────────────────────────────────

    [Fact]
    public void WS007_OrphanMember_EmitsWarning()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", "[project]\nname = \"foo\"\nkind = \"lib\"\n");
        // 在 apps/orphan 放一个未被 members 命中的 manifest
        Member("apps/orphan", "orphan.z42.toml", "[project]\nname = \"orphan\"\nkind = \"exe\"\nentry = \"O.main\"\n");

        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        var result = loader.LoadWorkspace(ctx);

        result.Warnings.Should().HaveCount(1);
        result.Warnings[0].Message.Should().Contain("WS007").And.Contain("orphan");
        result.Members.Should().HaveCount(1);
    }
}
