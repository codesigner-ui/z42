using FluentAssertions;
using Xunit;
using Z42.Pipeline;

namespace Z42.Tests;

/// opt-xtask-bootstrap-stdlib (2026-06-04): `BuildLibsDirs` must honor the
/// Z42_LIBS runtime lib-search env at COMPILE time too, added as a low-priority
/// fallback. This lets the CI xtask-bootstrap composite compile xtask.zpkg
/// against the downloaded prebuilt stdlib (`.z42/libs`) — which lives in none of
/// the fixed layout/walk-up candidates — instead of rebuilding stdlib from
/// source. Without it, `z42c build` scanned zero matching dirs → E0602.
public class BuildLibsDirsZ42LibsTests : IDisposable
{
    readonly string _origZ42Libs;
    readonly List<string> _tempDirs = new();

    public BuildLibsDirsZ42LibsTests()
    {
        _origZ42Libs = Environment.GetEnvironmentVariable("Z42_LIBS") ?? "";
    }

    public void Dispose()
    {
        // Restore the env and clean up temp dirs (this process is shared across tests).
        Environment.SetEnvironmentVariable("Z42_LIBS", _origZ42Libs.Length == 0 ? null : _origZ42Libs);
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
    }

    string NewTempDir(string label)
    {
        var d = Path.Combine(Path.GetTempPath(), $"z42-libsdirs-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        _tempDirs.Add(d);
        return d;
    }

    [Fact]
    public void Z42LibsDir_IsIncluded_WhenSet()
    {
        var project = NewTempDir("proj");
        var libs    = NewTempDir("libs");
        Environment.SetEnvironmentVariable("Z42_LIBS", libs);

        PackageCompiler.BuildLibsDirs(project).Should().Contain(libs);
    }

    [Fact]
    public void Z42LibsDir_NotIncluded_WhenUnset()
    {
        var project = NewTempDir("proj");
        var libs    = NewTempDir("libs");
        Environment.SetEnvironmentVariable("Z42_LIBS", null);

        PackageCompiler.BuildLibsDirs(project).Should().NotContain(libs);
    }

    [Fact]
    public void Z42Libs_IsAddedLast_AfterLayoutDirs()
    {
        // A project-local workspace-layout stdlib dir + a separate Z42_LIBS dir:
        // the layout dir must precede the Z42_LIBS dir (fallback priority).
        var project = NewTempDir("proj");
        var layoutDist = Path.Combine(project, "artifacts", "build", "libraries", "z42.core", "release", "dist");
        Directory.CreateDirectory(layoutDist);
        var libs = NewTempDir("libs");
        Environment.SetEnvironmentVariable("Z42_LIBS", libs);

        var dirs = PackageCompiler.BuildLibsDirs(project);
        dirs.Should().Contain(layoutDist);
        dirs.Should().Contain(libs);
        Array.IndexOf(dirs, layoutDist).Should().BeLessThan(Array.IndexOf(dirs, libs));
    }

    [Fact]
    public void Z42Libs_MultiplePaths_SplitOnPathSeparator()
    {
        var project = NewTempDir("proj");
        var a = NewTempDir("a");
        var b = NewTempDir("b");
        Environment.SetEnvironmentVariable("Z42_LIBS", a + Path.PathSeparator + b);

        var dirs = PackageCompiler.BuildLibsDirs(project);
        dirs.Should().Contain(a);
        dirs.Should().Contain(b);
    }

    [Fact]
    public void Z42Libs_NonexistentDir_Skipped()
    {
        var project = NewTempDir("proj");
        var missing = Path.Combine(Path.GetTempPath(), $"z42-libsdirs-missing-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("Z42_LIBS", missing);

        PackageCompiler.BuildLibsDirs(project).Should().NotContain(missing);
    }
}
