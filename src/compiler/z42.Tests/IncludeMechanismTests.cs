using FluentAssertions;
using Z42.Project;

namespace Z42.Tests;

/// 验证 C2 include 机制：路径解析、循环检测、深度限制、菱形去重、合并语义、preset 段限制。
public sealed class IncludeMechanismTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "z42_inc_" + Path.GetRandomFileName());

    public IncludeMechanismTests() => Directory.CreateDirectory(_root);
    public void Dispose()          => Directory.Delete(_root, recursive: true);

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

    void Preset(string relPath, string toml)
    {
        string full = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, toml);
    }

    ResolvedManifest LoadFooMember()
    {
        var loader = new ManifestLoader();
        var ctx = loader.DiscoverWorkspaceRoot(_root)!;
        return loader.LoadWorkspace(ctx).Members.Single();
    }

    // ── 基本 include + 合并语义 ─────────────────────────────────────────

    [Fact]
    public void Include_BasicMerge_PresetProvidesKindAndLicense()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Preset("presets/lib.toml", """
            [project]
            kind = "lib"
            license = "MIT"
            [sources]
            include = ["src/**/*.z42"]
            """);
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/lib.toml"]
            [project]
            name = "foo"
            """);

        var rm = LoadFooMember();
        rm.Kind.Should().Be(ProjectKind.Lib);
        rm.License.Should().Be("MIT");
        rm.Origins["[project].kind"].Kind.Should().Be(OriginKind.IncludePreset);
        rm.Origins["[project].license"].Kind.Should().Be(OriginKind.IncludePreset);
    }

    [Fact]
    public void Include_SelfOverridesPreset()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Preset("presets/lib.toml", "[project]\nkind = \"lib\"\nlicense = \"MIT\"\n");
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/lib.toml"]
            [project]
            name = "foo"
            license = "Apache-2.0"
            """);

        var rm = LoadFooMember();
        rm.License.Should().Be("Apache-2.0");
        rm.Origins["[project].license"].Kind.Should().Be(OriginKind.MemberDirect);
    }

    [Fact]
    public void Include_MultiplePresets_SecondOverridesFirst()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Preset("presets/a.toml", "[project]\nkind = \"lib\"\nlicense = \"MIT\"\n");
        Preset("presets/b.toml", "[project]\nlicense = \"Apache-2.0\"\n");
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/a.toml", "../../presets/b.toml"]
            [project]
            name = "foo"
            """);

        var rm = LoadFooMember();
        rm.Kind.Should().Be(ProjectKind.Lib);          // 来自 a
        rm.License.Should().Be("Apache-2.0");          // b 覆盖 a
    }

    [Fact]
    public void Include_ArrayWholeReplace_NotConcat()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Preset("presets/lib.toml", """
            [sources]
            include = ["src/**/*.z42"]
            """);
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/lib.toml"]
            [project]
            name = "foo"
            kind = "lib"
            [sources]
            include = ["src/**/*.z42", "extra/**/*.z42"]
            """);
        // 创建源文件让 sources 解析不报错
        File.WriteAllText(Path.Combine(_root, "libs/foo/src.dummy"), "");

        var rm = LoadFooMember();
        rm.Sources.Include.Should().BeEquivalentTo(new[] { "src/**/*.z42", "extra/**/*.z42" });
    }

    // ── 嵌套 include ─────────────────────────────────────────────────────

    [Fact]
    public void Include_NestedPreset()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Preset("presets/base.toml", "[project]\nkind = \"lib\"\nlicense = \"MIT\"\n");
        Preset("presets/strict.toml", """
            include = ["./base.toml"]
            [project]
            license = "Apache-2.0"
            """);
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/strict.toml"]
            [project]
            name = "foo"
            """);

        var rm = LoadFooMember();
        rm.Kind.Should().Be(ProjectKind.Lib);
        rm.License.Should().Be("Apache-2.0");          // strict 覆盖 base
    }

    // ── 菱形 include 去重 ──────────────────────────────────────────────────

    [Fact]
    public void Include_DiamondDedup()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Preset("presets/common.toml", "[project]\nkind = \"lib\"\n");
        Preset("presets/a.toml", """
            include = ["./common.toml"]
            [project]
            license = "MIT"
            """);
        Preset("presets/b.toml", """
            include = ["./common.toml"]
            """);
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/a.toml", "../../presets/b.toml"]
            [project]
            name = "foo"
            """);

        var rm = LoadFooMember();
        rm.Kind.Should().Be(ProjectKind.Lib);
        rm.License.Should().Be("MIT");
    }

    // ── WS020: 循环 ─────────────────────────────────────────────────────────

    [Fact]
    public void WS020_DirectCycle()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Preset("presets/self.toml", """
            include = ["./self.toml"]
            [project]
            kind = "lib"
            """);
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/self.toml"]
            [project]
            name = "foo"
            """);

        var act = () => LoadFooMember();
        act.Should().Throw<ManifestException>().WithMessage("*WS020*circular*");
    }

    [Fact]
    public void WS020_IndirectCycle()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Preset("presets/a.toml", """
            include = ["./b.toml"]
            """);
        Preset("presets/b.toml", """
            include = ["./a.toml"]
            """);
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/a.toml"]
            [project]
            name = "foo"
            kind = "lib"
            """);

        var act = () => LoadFooMember();
        act.Should().Throw<ManifestException>().WithMessage("*WS020*circular*");
    }

    // ── WS022: 深度限制 ─────────────────────────────────────────────────────

    [Fact]
    public void WS022_DepthExceeded()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        // 链 9 层超过 8 限制
        for (int i = 0; i < 9; i++)
        {
            string nextRef = i < 8 ? $"include = [\"./p{i + 1}.toml\"]\n" : "";
            Preset($"presets/p{i}.toml", nextRef + "[project]\nkind = \"lib\"\n");
        }
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/p0.toml"]
            [project]
            name = "foo"
            """);

        var act = () => LoadFooMember();
        act.Should().Throw<ManifestException>().WithMessage("*WS022*depth*");
    }

    // ── WS023: 路径不存在 ───────────────────────────────────────────────────

    [Fact]
    public void WS023_PathNotFound()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../missing.toml"]
            [project]
            name = "foo"
            kind = "lib"
            """);

        var act = () => LoadFooMember();
        act.Should().Throw<ManifestException>().WithMessage("*WS023*not found*");
    }

    // ── WS024: 路径不允许 ───────────────────────────────────────────────────

    [Fact]
    public void WS024_GlobNotAllowed()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/*.toml"]
            [project]
            name = "foo"
            kind = "lib"
            """);

        var act = () => LoadFooMember();
        act.Should().Throw<ManifestException>().WithMessage("*WS024*glob*");
    }

    [Fact]
    public void WS024_AbsolutePathNotAllowed()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", """
            include = ["/etc/preset.toml"]
            [project]
            name = "foo"
            kind = "lib"
            """);

        var act = () => LoadFooMember();
        act.Should().Throw<ManifestException>().WithMessage("*WS024*absolute*");
    }

    [Fact]
    public void WS024_UrlNotAllowed()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Member("libs/foo", "foo.z42.toml", """
            include = ["https://example.com/preset.toml"]
            [project]
            name = "foo"
            kind = "lib"
            """);

        var act = () => LoadFooMember();
        act.Should().Throw<ManifestException>().WithMessage("*WS024*URL*");
    }

    // ── WS021: preset 含禁用段 ─────────────────────────────────────────────

    [Fact]
    public void WS021_PresetHasWorkspaceSection()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Preset("presets/bad.toml", "[workspace]\nmembers = []\n");
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/bad.toml"]
            [project]
            name = "foo"
            kind = "lib"
            """);

        var act = () => LoadFooMember();
        act.Should().Throw<ManifestException>().WithMessage("*WS021*workspace*");
    }

    [Fact]
    public void WS021_PresetHasProfileSection()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Preset("presets/bad.toml", "[profile.release]\nmode = \"jit\"\n");
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/bad.toml"]
            [project]
            name = "foo"
            kind = "lib"
            """);

        var act = () => LoadFooMember();
        act.Should().Throw<ManifestException>().WithMessage("*WS021*profile*");
    }

    [Fact]
    public void WS021_PresetHasProjectName()
    {
        Workspace("[workspace]\nmembers = [\"libs/*\"]\n");
        Preset("presets/bad.toml", "[project]\nname = \"hijack\"\n");
        Member("libs/foo", "foo.z42.toml", """
            include = ["../../presets/bad.toml"]
            [project]
            name = "foo"
            kind = "lib"
            """);

        var act = () => LoadFooMember();
        act.Should().Throw<ManifestException>().WithMessage("*WS021*project.name*");
    }
}
