using FluentAssertions;
using Z42.Project;

namespace Z42.Tests;

public sealed class ProjectManifestTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ProjectManifestTests() => Directory.CreateDirectory(_dir);
    public void Dispose()        => Directory.Delete(_dir, recursive: true);

    // ── Helpers ───────────────────────────────────────────────────────────────

    ProjectManifest Load(string filename, string toml)
    {
        File.WriteAllText(Path.Combine(_dir, filename), toml);
        return ProjectManifest.Load(Path.Combine(_dir, filename));
    }

    // ── Discover ──────────────────────────────────────────────────────────────

    [Fact]
    public void Discover_SingleFile_ReturnsThatFile()
    {
        File.WriteAllText(Path.Combine(_dir, "hello.z42.toml"), "[project]\nkind=\"exe\"\nentry=\"Hello.main\"");
        var path = ProjectManifest.Discover(_dir);
        path.Should().EndWith("hello.z42.toml");
    }

    [Fact]
    public void Discover_NoFile_Throws()
    {
        var act = () => ProjectManifest.Discover(_dir);
        act.Should().Throw<ManifestException>().WithMessage("*no .z42.toml found*");
    }

    [Fact]
    public void Discover_MultipleFiles_Throws()
    {
        File.WriteAllText(Path.Combine(_dir, "a.z42.toml"), "[project]\nkind=\"lib\"");
        File.WriteAllText(Path.Combine(_dir, "b.z42.toml"), "[project]\nkind=\"lib\"");
        var act = () => ProjectManifest.Discover(_dir);
        act.Should().Throw<ManifestException>().WithMessage("*multiple .z42.toml*");
    }

    [Fact]
    public void Discover_ExplicitPath_UsesIt()
    {
        string path = Path.Combine(_dir, "myapp.z42.toml");
        File.WriteAllText(path, "[project]\nkind=\"exe\"\nentry=\"App.main\"");
        ProjectManifest.Discover(_dir, path).Should().Be(path);
    }

    // ── name inference ────────────────────────────────────────────────────────

    [Fact]
    public void Load_NameInferredFromFilename()
    {
        var m = Load("my-app.z42.toml", "[project]\nkind=\"lib\"");
        m.Project.Name.Should().Be("my-app");
    }

    [Fact]
    public void Load_NameExplicitOverridesFilename()
    {
        var m = Load("hello.z42.toml", "[project]\nname=\"override\"\nkind=\"lib\"");
        m.Project.Name.Should().Be("override");
    }

    // ── namespace inference ───────────────────────────────────────────────────

    [Fact]
    public void Load_NamespaceInferredFromKebabName()
    {
        var m = Load("my-cool-app.z42.toml", "[project]\nkind=\"lib\"");
        m.Project.Namespace.Should().Be("MyCoolApp");
    }

    [Fact]
    public void Load_NamespaceExplicitOverride()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"lib\"\nnamespace=\"Demo.Hello\"");
        m.Project.Namespace.Should().Be("Demo.Hello");
    }

    // ── kind validation ───────────────────────────────────────────────────────

    [Fact]
    public void Load_ExeWithEntry_Succeeds()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"exe\"\nentry=\"Hello.main\"");
        m.Project.Kind.Should().Be(ProjectKind.Exe);
        m.Project.Entry.Should().Be("Hello.main");
    }

    [Fact]
    public void Load_ExeWithoutEntry_Throws()
    {
        var act = () => Load("hello.z42.toml", "[project]\nkind=\"exe\"");
        act.Should().Throw<ManifestException>().WithMessage("*entry is required*");
    }

    [Fact]
    public void Load_LibWithoutEntry_Succeeds()
    {
        var m = Load("mylib.z42.toml", "[project]\nkind=\"lib\"");
        m.Project.Kind.Should().Be(ProjectKind.Lib);
        m.Project.Entry.Should().BeNull();
    }

    [Fact]
    public void Load_InvalidKind_Throws()
    {
        var act = () => Load("hello.z42.toml", "[project]\nkind=\"invalid\"");
        act.Should().Throw<ManifestException>().WithMessage("*kind must be*");
    }

    // ── ResolvePack priority chain ────────────────────────────────────────────

    [Fact]
    public void ResolvePack_NoConfig_DebugFalseRelaseTrue()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"exe\"\nentry=\"Hello.main\"");
        m.ResolvePack(releaseProfile: false).Should().BeFalse();
        m.ResolvePack(releaseProfile: true).Should().BeTrue();
    }

    [Fact]
    public void ResolvePack_ProjectPackTrue_OverridesDefault()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"exe\"\nentry=\"Hello.main\"\npack=true");
        m.ResolvePack(releaseProfile: false).Should().BeTrue();
        m.ResolvePack(releaseProfile: true).Should().BeTrue();
    }

    [Fact]
    public void ResolvePack_ProfilePackOverridesProject()
    {
        var m = Load("hello.z42.toml", """
            [project]
            kind  = "exe"
            entry = "Hello.main"
            pack  = true

            [profile.debug]
            pack = false
            """);
        m.ResolvePack(releaseProfile: false).Should().BeFalse();  // profile wins
        m.ResolvePack(releaseProfile: true).Should().BeTrue();    // project default
    }

    [Fact]
    public void ResolvePack_TargetPackOverridesProject()
    {
        var m = Load("myapp.z42.toml", """
            [project]
            pack = false

            [[exe]]
            name  = "tool"
            entry = "Tool.main"
            pack  = true
            """);
        var target = m.ExeTargets[0];
        m.ResolvePack(releaseProfile: false, targetPack: target.Pack).Should().BeTrue();
    }

    [Fact]
    public void ResolvePack_ProfileOverridesTargetPack()
    {
        var m = Load("myapp.z42.toml", """
            [project]
            [[exe]]
            name  = "tool"
            entry = "Tool.main"
            pack  = true

            [profile.debug]
            pack = false
            """);
        var target = m.ExeTargets[0];
        m.ResolvePack(releaseProfile: false, targetPack: target.Pack).Should().BeFalse();
    }

    // ── sources defaults ──────────────────────────────────────────────────────

    [Fact]
    public void Load_SourcesDefaults_WhenSectionAbsent()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"lib\"");
        m.Sources.Include.Should().Equal("src/**/*.z42");
        m.Sources.Exclude.Should().BeEmpty();
    }

    // ── profile defaults ──────────────────────────────────────────────────────

    [Fact]
    public void Load_DebugProfileDefaults_WhenSectionAbsent()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"lib\"");
        m.Debug.Mode.Should().Be("interp");
        m.Debug.Optimize.Should().Be(0);
        m.Debug.Debug.Should().BeTrue();
    }

    [Fact]
    public void Load_ReleaseProfileDefaults_WhenSectionAbsent()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"lib\"");
        m.Release.Mode.Should().Be("jit");
        m.Release.Optimize.Should().Be(3);
        m.Release.Strip.Should().BeTrue();
    }

    [Fact]
    public void SelectProfile_ReturnDebugByDefault()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"lib\"");
        m.SelectProfile(release: false).Should().Be(m.Debug);
        m.SelectProfile(release: true).Should().Be(m.Release);
    }

    // ── unknown fields ignored ────────────────────────────────────────────────

    [Fact]
    public void Load_UnknownFields_Ignored()
    {
        var act = () => Load("hello.z42.toml",
            "[project]\nkind=\"lib\"\nunknown_field=\"foo\"\n[extra_section]\nfoo=1");
        act.Should().NotThrow();
    }

    // ── [[exe]] multi-target ──────────────────────────────────────────────────

    [Fact]
    public void Load_MultiExe_ParsesTwoTargets()
    {
        var m = Load("myapp.z42.toml", """
            [project]
            version = "0.1.0"

            [[exe]]
            name  = "hello"
            entry = "Hello.main"

            [[exe]]
            name  = "tool"
            entry = "Tool.main"
            """);
        m.Project.Kind.Should().Be(ProjectKind.Multi);
        m.ExeTargets.Should().HaveCount(2);
        m.ExeTargets[0].Name.Should().Be("hello");
        m.ExeTargets[0].Entry.Should().Be("Hello.main");
        m.ExeTargets[1].Name.Should().Be("tool");
        m.ExeTargets[1].Entry.Should().Be("Tool.main");
    }

    [Fact]
    public void Load_MultiExe_WithSrcOverride()
    {
        var m = Load("myapp.z42.toml", """
            [project]
            [[exe]]
            name  = "tool"
            entry = "Tool.main"
            src   = ["src/tool/**/*.z42"]
            """);
        m.ExeTargets[0].Src.Should().Equal("src/tool/**/*.z42");
    }

    [Fact]
    public void Load_MultiExe_WithoutSrc_InheritsSharedSources()
    {
        var m = Load("myapp.z42.toml", """
            [project]
            [sources]
            include = ["lib/**/*.z42"]
            [[exe]]
            name  = "hello"
            entry = "Hello.main"
            """);
        m.ExeTargets[0].Src.Should().BeNullOrEmpty();
        m.Sources.Include.Should().Equal("lib/**/*.z42");
    }

    [Fact]
    public void Load_MultiExe_MissingName_Throws()
    {
        var act = () => Load("myapp.z42.toml", """
            [project]
            [[exe]]
            entry = "Hello.main"
            """);
        act.Should().Throw<ManifestException>().WithMessage("*missing required field 'name'*");
    }

    [Fact]
    public void Load_MultiExe_MissingEntry_Throws()
    {
        var act = () => Load("myapp.z42.toml", """
            [project]
            [[exe]]
            name = "hello"
            """);
        act.Should().Throw<ManifestException>().WithMessage("*missing required field 'entry'*");
    }

    [Fact]
    public void Load_MultiExe_ConflictsWithKindExe_Throws()
    {
        var act = () => Load("myapp.z42.toml", """
            [project]
            kind  = "exe"
            entry = "Hello.main"
            [[exe]]
            name  = "hello"
            entry = "Hello.main"
            """);
        act.Should().Throw<ManifestException>().WithMessage("*cannot use [[exe]] together*");
    }

    [Fact]
    public void Load_SingleExeTarget_Succeeds()
    {
        var m = Load("myapp.z42.toml", """
            [project]
            [[exe]]
            name  = "hello"
            entry = "Hello.main"
            """);
        m.Project.Kind.Should().Be(ProjectKind.Multi);
        m.ExeTargets.Should().HaveCount(1);
    }

    [Fact]
    public void Load_KindExeWithoutExeTable_BackwardCompat()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"exe\"\nentry=\"Hello.main\"");
        m.Project.Kind.Should().Be(ProjectKind.Exe);
        m.ExeTargets.Should().BeEmpty();
    }
}
