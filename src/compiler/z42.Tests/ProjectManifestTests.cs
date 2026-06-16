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

    // ── kind validation ───────────────────────────────────────────────────────

    [Fact]
    public void Load_ExeWithEntry_Succeeds()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"exe\"\nentry=\"Hello.main\"");
        m.Project.Kind.Should().Be(ProjectKind.Exe);
        m.Project.Entry.Should().Be("Hello.main");
    }

    // 2026-05-14 auto-detect-main: `[project].entry` is now optional for exe.
    // PackageCompiler reports missing-Main, not the manifest loader.
    [Fact]
    public void Load_ExeWithoutEntry_LeavesEntryNull()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"exe\"");
        m.Project.Kind.Should().Be(ProjectKind.Exe);
        m.Project.Entry.Should().BeNull();
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

    // ── [build] output_dir / cache_dir / dist_dir (restructure-build-output-dirs) ──

    [Fact]
    public void Build_AllThreeDirs_ParsedWhenSet()
    {
        var m = Load("hello.z42.toml", """
            [project]
            kind  = "exe"
            entry = "Hello.main"

            [build]
            output_dir = "../artifacts/hello"
            dist_dir   = "../artifacts/hello/dist"
            cache_dir  = "../artifacts/hello/.cache"
            """);
        m.Build.OutputDir.Should().Be("../artifacts/hello");
        m.Build.DistDir.Should().Be("../artifacts/hello/dist");
        m.Build.CacheDir.Should().Be("../artifacts/hello/.cache");
    }

    [Fact]
    public void Build_AllThreeDirs_NullWhenOmitted()
    {
        // restructure-build-output-dirs (2026-06-06): unset = null (raw);
        // effective paths come from CentralizedBuildLayout's cascade
        // defaults, not from BuildSection field defaults.
        var m = Load("hello.z42.toml", """
            [project]
            kind  = "exe"
            entry = "Hello.main"
            """);
        m.Build.OutputDir.Should().BeNull();
        m.Build.CacheDir.Should().BeNull();
        m.Build.DistDir.Should().BeNull();
    }

    [Fact]
    public void Build_LegacyOutDir_TriggersWS008()
    {
        // restructure-build-output-dirs (2026-06-06): old `out_dir` field
        // retired; appears as WS008 unknown-key with Levenshtein
        // suggestion `dist_dir`.
        var result = LoadWithWarnings("hello.z42.toml", """
            [project]
            kind = "exe"
            entry = "Hello.main"

            [build]
            out_dir = "dist"
            """);
        result.Warnings.Should().Contain(w =>
            w.Message.Contains("out_dir") && w.Message.Contains("dist_dir"));
    }

    [Fact]
    public void DesktopPlatform_KnownSection_NoWarning()
    {
        // apphost-as-config (2026-06-17): [platform.desktop] + publish_dir are a
        // known section/key (consumed by `z42 export desktop`, not z42c) → no
        // WS008. Replaces the retired [apphost] section.
        var result = LoadWithWarnings("xtask.z42.toml", """
            [project]
            name = "xtask"
            kind = "exe"
            entry = "Xtask.Main"

            [platform.desktop]
            publish_dir = ".."
            """);
        result.Warnings.Should().NotContain(w => w.Message.Contains("desktop"));
        result.Warnings.Should().NotContain(w => w.Message.Contains("publish_dir"));
    }

    [Fact]
    public void DesktopPlatform_StrayKey_TriggersWS008()
    {
        var result = LoadWithWarnings("xtask.z42.toml", """
            [project]
            name = "xtask"
            kind = "exe"
            entry = "Xtask.Main"

            [platform.desktop]
            publish_dir = ".."
            bogus_key = 1
            """);
        result.Warnings.Should().Contain(w =>
            w.Message.Contains("bogus_key") && w.Message.Contains("platform.desktop"));
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

    // 2026-05-14 auto-detect-main: `[[exe]].entry` is optional too.
    [Fact]
    public void Load_MultiExe_MissingEntry_LeavesEntryNull()
    {
        var m = Load("myapp.z42.toml", """
            [project]
            [[exe]]
            name = "hello"
            """);
        m.ExeTargets.Should().HaveCount(1);
        m.ExeTargets[0].Name.Should().Be("hello");
        m.ExeTargets[0].Entry.Should().BeNull();
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

    // ── [dependencies] ───────────────────────────────────────────────────────

    [Fact]
    public void Load_NoDependencies_AutoScanMode()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"lib\"");
        m.Dependencies.IsDeclared.Should().BeFalse();
        m.Dependencies.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Load_EmptyDependencies_DeclaredButEmpty()
    {
        var m = Load("hello.z42.toml", "[project]\nkind=\"lib\"\n[dependencies]");
        m.Dependencies.IsDeclared.Should().BeTrue();
        m.Dependencies.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Load_WithDependencies_ParsesNameAndVersion()
    {
        var m = Load("hello.z42.toml", """
            [project]
            kind = "exe"
            entry = "Hello.main"
            [dependencies]
            "my-utils" = "*"
            "my-http"  = "1.2.0"
            """);
        m.Dependencies.IsDeclared.Should().BeTrue();
        m.Dependencies.Entries.Should().HaveCount(2);
        m.Dependencies.Entries[0].Name.Should().Be("my-utils");
        m.Dependencies.Entries[0].Version.Should().Be("*");
        m.Dependencies.Entries[1].Name.Should().Be("my-http");
        m.Dependencies.Entries[1].Version.Should().Be("1.2.0");
    }

    [Fact]
    public void Load_StdlibDependency_ParsedNormally()
    {
        var m = Load("mylib.z42.toml", """
            [project]
            kind = "lib"
            [dependencies]
            "z42.core" = "0.1.0"
            """);
        m.Dependencies.IsDeclared.Should().BeTrue();
        m.Dependencies.Entries.Should().ContainSingle()
            .Which.Name.Should().Be("z42.core");
    }

    // ── add-manifest-hygiene-warnings (WS008) — unknown-key scan ──────────────

    ProjectManifestLoadResult LoadWithWarnings(string filename, string toml)
    {
        File.WriteAllText(Path.Combine(_dir, filename), toml);
        return ProjectManifest.LoadWithWarnings(Path.Combine(_dir, filename));
    }

    [Fact]
    public void LoadWithWarnings_KnownKeysOnly_NoWarnings()
    {
        // restructure-build-output-dirs (2026-06-06): `out_dir` is now an
        // unknown key (replaced by `output_dir` / `cache_dir` / `dist_dir`).
        var r = LoadWithWarnings("clean.z42.toml", """
            [project]
            name = "clean"
            version = "0.1.0"
            kind = "exe"
            entry = "Clean.Main"
            description = "all known keys"
            pack = true
            [sources]
            include = ["**/*.z42"]
            exclude = []
            [build]
            output_dir = ".."
            dist_dir = "../dist"
            cache_dir = "../artifacts/clean/.cache"
            mode = "interp"
            incremental = true
            [profile.release]
            mode = "jit"
            optimize = 3
            debug = false
            strip = true
            pack = true
            """);
        r.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void LoadWithWarnings_UnknownProjectKey_EmitsWS008()
    {
        var r = LoadWithWarnings("typo.z42.toml", """
            [project]
            name = "typo"
            kind = "exe"
            entrypoint = "Hello.Main"
            """);
        r.Warnings.Should().ContainSingle()
            .Which.Message.Should().Contain("warning[WS008]")
            .And.Contain("unknown key 'entrypoint' in [project]");
    }

    [Fact]
    public void LoadWithWarnings_TypoNearKnownKey_SuggestsCorrection()
    {
        var r = LoadWithWarnings("near.z42.toml", """
            [project]
            kind = "exe"
            [builds]
            out_dir = "dist"
            """);
        r.Warnings.Should().Contain(w =>
            w.Message.Contains("WS008") &&
            w.Message.Contains("'builds'") &&
            w.Message.Contains("did you mean 'build'"));
    }

    [Fact]
    public void LoadWithWarnings_UnknownTopLevelKey_EmitsWS008()
    {
        var r = LoadWithWarnings("topkey.z42.toml", """
            [project]
            kind = "exe"
            [random_section]
            x = 1
            """);
        r.Warnings.Should().Contain(w =>
            w.Message.Contains("WS008") &&
            w.Message.Contains("'random_section'") &&
            w.Message.Contains("(top-level)"));
    }

    [Fact]
    public void LoadWithWarnings_UnknownExeKey_EmitsWS008()
    {
        // [[exe]] with [project] (kind omitted → inferred as Multi).
        var r = LoadWithWarnings("exe.z42.toml", """
            [project]
            name = "x"
            [[exe]]
            name = "a"
            entry = "A.Main"
            bogus = true
            """);
        r.Warnings.Should().Contain(w =>
            w.Message.Contains("WS008") &&
            w.Message.Contains("'bogus'"));
    }

    [Fact]
    public void LoadWithWarnings_DependenciesSection_NoFalsePositives()
    {
        // [dependencies] keys are package names — any string is valid.
        // (Third-party names only: stdlib `z42.*` deps would trigger WS013
        // under simplify-stdlib-auto-import; covered by dedicated WS013 tests.)
        var r = LoadWithWarnings("deps.z42.toml", """
            [project]
            kind = "lib"
            [dependencies]
            "my-utils"     = "*"
            "acme.widgets" = "1.0"
            """);
        r.Warnings.Should().BeEmpty();
    }
}
