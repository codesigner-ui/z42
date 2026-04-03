using FluentAssertions;
using Z42.Build;

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

    // ── emit inference ────────────────────────────────────────────────────────

    [Fact]
    public void Load_ExeEmitDefaultsToZbc()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"exe\"\nentry=\"Hello.main\"");
        m.Build.Emit.Should().Be("zbc");
    }

    [Fact]
    public void Load_LibEmitDefaultsToZlib()
    {
        var m = Load("mylib.z42.toml", "[project]\nkind=\"lib\"");
        m.Build.Emit.Should().Be("zlib");
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
}
