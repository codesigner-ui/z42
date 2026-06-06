using FluentAssertions;
using Z42.Project;

namespace Z42.Tests;

// add-tests-bench-manifest-config (2026-06-06): exercises the new [tests],
// [bench], [[test]], [[bench]] sections + WS012/WS040-WS043 error codes.
// Split from ProjectManifestTests (over 500-line code-organization limit)
// to keep each test file focused.
public sealed class TestBenchManifestTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public TestBenchManifestTests() => Directory.CreateDirectory(_dir);
    public void Dispose()            => Directory.Delete(_dir, recursive: true);

    ProjectManifest Load(string filename, string toml)
    {
        File.WriteAllText(Path.Combine(_dir, filename), toml);
        return ProjectManifest.Load(Path.Combine(_dir, filename));
    }

    ProjectManifestLoadResult LoadWithWarnings(string filename, string toml)
    {
        File.WriteAllText(Path.Combine(_dir, filename), toml);
        return ProjectManifest.LoadWithWarnings(Path.Combine(_dir, filename));
    }

    // Helper: write an empty .z42 src file so [[test]] WS043 checks pass.
    void Touch(string relPath)
    {
        string full = Path.Combine(_dir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "void Main() { }");
    }

    // ── Defaults: when [tests] / [bench] omitted, convention globs apply ──

    [Fact]
    public void DefaultTestsInclude_IsConventionGlob()
    {
        var m = Load("a.z42.toml", """
            [project]
            kind = "lib"
            """);
        m.Tests.Include.Should().BeEquivalentTo(new[] { "tests/*.z42", "tests/*/source.z42" });
        m.Tests.Exclude.Should().BeEmpty();
        m.Tests.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void DefaultBenchInclude_IsConventionGlob()
    {
        var m = Load("a.z42.toml", """
            [project]
            kind = "lib"
            """);
        m.Bench.Include.Should().BeEquivalentTo(new[] { "bench/*.z42", "bench/*/source.z42" });
        m.Bench.Exclude.Should().BeEmpty();
        m.Bench.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void NoTestEntries_NoBenchEntries_ByDefault()
    {
        var m = Load("a.z42.toml", """
            [project]
            kind = "lib"
            """);
        m.TestEntries.Should().BeEmpty();
        m.BenchEntries.Should().BeEmpty();
    }

    // ── [tests] section parsing ──────────────────────────────────────────

    [Fact]
    public void TestsSection_ExplicitInclude_Overrides()
    {
        var m = Load("a.z42.toml", """
            [project]
            kind = "lib"
            [tests]
            include = ["tests/integration/*.z42"]
            exclude = ["tests/_skip/*"]
            """);
        m.Tests.Include.Should().BeEquivalentTo(new[] { "tests/integration/*.z42" });
        m.Tests.Exclude.Should().BeEquivalentTo(new[] { "tests/_skip/*" });
    }

    [Fact]
    public void TestsDependencies_Parses()
    {
        var m = Load("a.z42.toml", """
            [project]
            kind = "lib"
            [tests.dependencies]
            "z42.test" = "0.1.0"
            "z42.text" = "0.2.0"
            """);
        m.Tests.Dependencies.Should().HaveCount(2);
        m.Tests.Dependencies.Should().Contain(d => d.Name == "z42.test" && d.Version == "0.1.0");
        m.Tests.Dependencies.Should().Contain(d => d.Name == "z42.text" && d.Version == "0.2.0");
    }

    [Fact]
    public void BenchDependencies_Parses()
    {
        var m = Load("a.z42.toml", """
            [project]
            kind = "lib"
            [bench.dependencies]
            "z42.test" = "0.1.0"
            """);
        m.Bench.Dependencies.Should().HaveCount(1);
        m.Bench.Dependencies[0].Name.Should().Be("z42.test");
    }

    // ── [[test]] / [[bench]] array parsing ───────────────────────────────

    [Fact]
    public void TestEntry_BasicShape_Parses()
    {
        Touch("tests/perf/runner.z42");
        var m = Load("a.z42.toml", """
            [project]
            kind = "lib"
            [[test]]
            name = "compile_perf"
            src  = "tests/perf/runner.z42"
            sources = ["tests/perf/*.z42", "tests/perf/_lib/*.z42"]
            """);
        m.TestEntries.Should().HaveCount(1);
        var e = m.TestEntries[0];
        e.Name.Should().Be("compile_perf");
        e.Src.Should().Be("tests/perf/runner.z42");
        e.Sources.Should().BeEquivalentTo(new[]
            { "tests/perf/*.z42", "tests/perf/_lib/*.z42" });
    }

    [Fact]
    public void TestEntry_WithDependencies_Parses()
    {
        Touch("tests/perf/runner.z42");
        var m = Load("a.z42.toml", """
            [project]
            kind = "lib"
            [[test]]
            name = "compile_perf"
            src  = "tests/perf/runner.z42"
            [test.dependencies]
            "z42.compression" = "0.1.0"
            """);
        m.TestEntries[0].Dependencies.Should().HaveCount(1);
        m.TestEntries[0].Dependencies[0].Name.Should().Be("z42.compression");
    }

    [Fact]
    public void BenchEntry_BasicShape_Parses()
    {
        Touch("bench/manifest_parse.z42");
        var m = Load("a.z42.toml", """
            [project]
            kind = "lib"
            [[bench]]
            name = "manifest_parse"
            src  = "bench/manifest_parse.z42"
            """);
        m.BenchEntries.Should().HaveCount(1);
        m.BenchEntries[0].Name.Should().Be("manifest_parse");
        m.BenchEntries[0].Sources.Should().BeEmpty();
    }

    [Fact]
    public void MultipleTestEntries_AllParse()
    {
        Touch("tests/a/source.z42");
        Touch("tests/b/source.z42");
        var m = Load("a.z42.toml", """
            [project]
            kind = "lib"
            [[test]]
            name = "first"
            src  = "tests/a/source.z42"
            [[test]]
            name = "second"
            src  = "tests/b/source.z42"
            """);
        m.TestEntries.Should().HaveCount(2);
        m.TestEntries.Select(e => e.Name).Should().BeEquivalentTo(new[] { "first", "second" });
    }

    // ── WS040 (missing name) ─────────────────────────────────────────────

    [Fact]
    public void TestEntry_MissingName_ThrowsWS040()
    {
        Touch("tests/anon.z42");
        var act = () => Load("a.z42.toml", """
            [project]
            kind = "lib"
            [[test]]
            src = "tests/anon.z42"
            """);
        act.Should().Throw<ManifestException>()
            .Where(e => e.Message.Contains("WS040") && e.Message.Contains("'name'"));
    }

    [Fact]
    public void BenchEntry_MissingName_ThrowsWS040()
    {
        Touch("bench/anon.z42");
        var act = () => Load("a.z42.toml", """
            [project]
            kind = "lib"
            [[bench]]
            src = "bench/anon.z42"
            """);
        act.Should().Throw<ManifestException>()
            .Where(e => e.Message.Contains("WS040"));
    }

    // ── WS041 (missing src) ──────────────────────────────────────────────

    [Fact]
    public void TestEntry_MissingSrc_ThrowsWS041()
    {
        var act = () => Load("a.z42.toml", """
            [project]
            kind = "lib"
            [[test]]
            name = "nameonly"
            """);
        act.Should().Throw<ManifestException>()
            .Where(e => e.Message.Contains("WS041") && e.Message.Contains("'src'")
                     && e.Message.Contains("nameonly"));
    }

    // ── WS042 (duplicate name) ───────────────────────────────────────────

    [Fact]
    public void DuplicateTestNames_ThrowsWS042()
    {
        Touch("tests/a.z42");
        Touch("tests/b.z42");
        var act = () => Load("a.z42.toml", """
            [project]
            kind = "lib"
            [[test]]
            name = "same"
            src  = "tests/a.z42"
            [[test]]
            name = "same"
            src  = "tests/b.z42"
            """);
        act.Should().Throw<ManifestException>()
            .Where(e => e.Message.Contains("WS042") && e.Message.Contains("'same'"));
    }

    [Fact]
    public void DuplicateBenchNames_ThrowsWS042()
    {
        Touch("bench/a.z42");
        Touch("bench/b.z42");
        var act = () => Load("a.z42.toml", """
            [project]
            kind = "lib"
            [[bench]]
            name = "dup"
            src  = "bench/a.z42"
            [[bench]]
            name = "dup"
            src  = "bench/b.z42"
            """);
        act.Should().Throw<ManifestException>()
            .Where(e => e.Message.Contains("WS042"));
    }

    [Fact]
    public void SameNameAcrossTestAndBench_Allowed()
    {
        // [[test]] name="x" and [[bench]] name="x" do NOT collide — each
        // namespace (test / bench) tracks its own seen-names set.
        Touch("tests/x.z42");
        Touch("bench/x.z42");
        var m = Load("a.z42.toml", """
            [project]
            kind = "lib"
            [[test]]
            name = "x"
            src  = "tests/x.z42"
            [[bench]]
            name = "x"
            src  = "bench/x.z42"
            """);
        m.TestEntries[0].Name.Should().Be("x");
        m.BenchEntries[0].Name.Should().Be("x");
    }

    // ── WS043 (src not found) ────────────────────────────────────────────

    [Fact]
    public void TestEntry_SrcNotFound_ThrowsWS043()
    {
        var act = () => Load("a.z42.toml", """
            [project]
            kind = "lib"
            [[test]]
            name = "missing_src"
            src  = "tests/does_not_exist.z42"
            """);
        act.Should().Throw<ManifestException>()
            .Where(e => e.Message.Contains("WS043")
                     && e.Message.Contains("tests/does_not_exist.z42"));
    }

    // ── WS012 (test-only dep leak) ───────────────────────────────────────

    [Fact]
    public void WS012_TestDepInProductionDeps_Warns()
    {
        var r = LoadWithWarnings("a.z42.toml", """
            [project]
            kind = "lib"
            [dependencies]
            "z42.test" = "0.1.0"
            """);
        r.Warnings.Should().Contain(w =>
            w.Message.Contains("WS012") &&
            w.Message.Contains("z42.test") &&
            w.Message.Contains("tests.dependencies"));
    }

    [Fact]
    public void WS012_TestDepInTestsDeps_NoWarning()
    {
        var r = LoadWithWarnings("a.z42.toml", """
            [project]
            kind = "lib"
            [tests.dependencies]
            "z42.test" = "0.1.0"
            """);
        r.Warnings.Where(w => w.Message.Contains("WS012")).Should().BeEmpty();
    }

    [Fact]
    public void WS012_TestDepInBenchDeps_NoWarning()
    {
        var r = LoadWithWarnings("a.z42.toml", """
            [project]
            kind = "lib"
            [bench.dependencies]
            "z42.test" = "0.1.0"
            """);
        r.Warnings.Where(w => w.Message.Contains("WS012")).Should().BeEmpty();
    }

    [Fact]
    public void WS012_NormalDeps_NoWarning()
    {
        // z42.core / z42.text aren't test-only → no WS012. (They ARE stdlib,
        // so simplify-stdlib-auto-import's WS013 fires — verified below.)
        var r = LoadWithWarnings("a.z42.toml", """
            [project]
            kind = "lib"
            [dependencies]
            "z42.core" = "0.1.0"
            "z42.text" = "0.1.0"
            """);
        r.Warnings.Where(w => w.Message.Contains("WS012")).Should().BeEmpty();
        r.Warnings.Should().Contain(w => w.Message.Contains("WS013"));
    }

    // ── WS013 (redundant stdlib dependency) ──────────────────────────────

    [Fact]
    public void WS013_UserProjectDeclaresStdlib_Warns()
    {
        var r = LoadWithWarnings("demo.z42.toml", """
            [project]
            name = "demo"
            kind = "exe"
            [dependencies]
            "z42.io" = "0.1.0"
            """);
        r.Warnings.Should().Contain(w =>
            w.Message.Contains("WS013") &&
            w.Message.Contains("z42.io") &&
            w.Message.Contains("dependencies"));
    }

    [Fact]
    public void WS013_UserProjectStdlibInTestsDeps_Warns()
    {
        var r = LoadWithWarnings("demo.z42.toml", """
            [project]
            name = "demo"
            kind = "exe"
            [tests.dependencies]
            "z42.test" = "0.1.0"
            """);
        r.Warnings.Should().Contain(w =>
            w.Message.Contains("WS013") &&
            w.Message.Contains("tests.dependencies"));
    }

    [Fact]
    public void WS013_StdlibPackageInterDep_Exempt()
    {
        // A z42.* package's inter-package dep is a genuine build-order edge,
        // not a redundant stdlib declaration → no WS013.
        var r = LoadWithWarnings("z42.io.z42.toml", """
            [project]
            name = "z42.io"
            kind = "lib"
            [dependencies]
            "z42.time" = "0.1.0"
            """);
        r.Warnings.Where(w => w.Message.Contains("WS013")).Should().BeEmpty();
    }

    [Fact]
    public void WS013_ThirdPartyDep_NoWarning()
    {
        var r = LoadWithWarnings("demo.z42.toml", """
            [project]
            name = "demo"
            kind = "exe"
            [dependencies]
            "acme.widgets" = "1.0"
            """);
        r.Warnings.Where(w => w.Message.Contains("WS013")).Should().BeEmpty();
    }

    // ── WS012 suppression for synthetic harness projects ────────────────
    //
    // xtask's dir-mode path generates `<lib>.test.<unit>` / `<lib>.bench.
    // <unit>` mini-manifests that legitimately depend on z42.test (the
    // harness IS the test framework). The IsSyntheticHarnessProject check
    // skips WS012 for names containing ".test." or ".bench." infix —
    // matches the same naming convention enforced by xtask + the CI
    // release-guard regex.

    [Fact]
    public void WS012_SuppressedForSyntheticTestHarness()
    {
        // [project].name explicitly matches xtask's synthetic naming.
        var r = LoadWithWarnings("z42.json.test.parse_basic.z42.toml", """
            [project]
            name = "z42.json.test.parse_basic"
            kind = "exe"
            [dependencies]
            "z42.test" = "0.1.0"
            """);
        r.Warnings.Where(w => w.Message.Contains("WS012")).Should().BeEmpty();
    }

    [Fact]
    public void WS012_SuppressedForSyntheticBenchHarness()
    {
        var r = LoadWithWarnings("z42.numerics.bench.bigint_pow.z42.toml", """
            [project]
            name = "z42.numerics.bench.bigint_pow"
            kind = "exe"
            [dependencies]
            "z42.test" = "0.1.0"
            """);
        r.Warnings.Where(w => w.Message.Contains("WS012")).Should().BeEmpty();
    }

    [Fact]
    public void WS012_FiresForNamesWithoutInfix()
    {
        // 'testing' has no ".test." infix (no dot after 'test') — must
        // still warn. Sentinel against a too-loose substring impl.
        var r = LoadWithWarnings("a.z42.toml", """
            [project]
            name = "myorg.testing"
            kind = "lib"
            [dependencies]
            "z42.test" = "0.1.0"
            """);
        r.Warnings.Should().Contain(w => w.Message.Contains("WS012"));
    }

    // ── KnownTopLevelKeys covers tests/bench/test/benchmark ─────────────

    [Fact]
    public void TestsTopLevel_NoUnknownKeyWarning()
    {
        var r = LoadWithWarnings("a.z42.toml", """
            [project]
            kind = "lib"
            [tests]
            include = ["tests/*.z42"]
            """);
        r.Warnings.Where(w => w.Message.Contains("WS008")).Should().BeEmpty();
    }

    [Fact]
    public void BenchTopLevel_NoUnknownKeyWarning()
    {
        var r = LoadWithWarnings("a.z42.toml", """
            [project]
            kind = "lib"
            [bench]
            include = ["bench/*.z42"]
            """);
        r.Warnings.Where(w => w.Message.Contains("WS008")).Should().BeEmpty();
    }

    [Fact]
    public void TestArrayTopLevel_NoUnknownKeyWarning()
    {
        Touch("tests/x.z42");
        var r = LoadWithWarnings("a.z42.toml", """
            [project]
            kind = "lib"
            [[test]]
            name = "x"
            src  = "tests/x.z42"
            """);
        r.Warnings.Where(w => w.Message.Contains("WS008")).Should().BeEmpty();
    }

    // ── Unknown keys inside [tests] / [[test]] emit WS008 ────────────────

    [Fact]
    public void UnknownKeyInTests_EmitsWS008()
    {
        var r = LoadWithWarnings("a.z42.toml", """
            [project]
            kind = "lib"
            [tests]
            bogus = "value"
            """);
        r.Warnings.Should().Contain(w =>
            w.Message.Contains("WS008") &&
            w.Message.Contains("'bogus'") &&
            w.Message.Contains("[tests]"));
    }

    [Fact]
    public void UnknownKeyInTestEntry_EmitsWS008()
    {
        Touch("tests/x.z42");
        var r = LoadWithWarnings("a.z42.toml", """
            [project]
            kind = "lib"
            [[test]]
            name = "x"
            src  = "tests/x.z42"
            bogus = "value"
            """);
        r.Warnings.Should().Contain(w =>
            w.Message.Contains("WS008") && w.Message.Contains("'bogus'"));
    }
}
