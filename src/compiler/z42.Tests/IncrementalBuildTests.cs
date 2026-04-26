using FluentAssertions;
using Z42.Pipeline;
using IncrementalBuild = Z42.Pipeline.IncrementalBuild;

namespace Z42.Tests;

/// 验证 IncrementalBuild.Probe 的失效条件分流（cached vs fresh）。
public sealed class IncrementalBuildTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "z42_incr_" + Path.GetRandomFileName());

    public IncrementalBuildTests() => Directory.CreateDirectory(_tmp);
    public void Dispose()           => Directory.Delete(_tmp, recursive: true);

    [Fact]
    public void Probe_NoLastZpkg_AllFresh()
    {
        var sources = new[] { Path.Combine(_tmp, "A.z42"), Path.Combine(_tmp, "B.z42") };
        File.WriteAllText(sources[0], "namespace A;");
        File.WriteAllText(sources[1], "namespace B;");

        var probe = new IncrementalBuild().Probe(
            sources, _tmp,
            cacheDir:     Path.Combine(_tmp, "cache"),
            lastZpkgPath: Path.Combine(_tmp, "missing.zpkg"));

        probe.CachedCount.Should().Be(0);
        probe.TotalCount.Should().Be(2);
        probe.FreshFiles.Should().BeEquivalentTo(sources);
    }

    [Fact]
    public void Probe_AllFresh_StaticConstructor()
    {
        var files = new[] { "/a.z42", "/b.z42" };
        var p = IncrementalBuild.ProbeResult.AllFresh(files);
        p.CachedCount.Should().Be(0);
        p.TotalCount.Should().Be(2);
        p.FreshFiles.Should().BeEquivalentTo(files);
        p.CachedZbcByFile.Should().BeEmpty();
        p.CachedExportsByNs.Should().BeEmpty();
    }

    [Fact]
    public void Probe_CorruptedZpkg_AllFresh()
    {
        string zpkg = Path.Combine(_tmp, "bad.zpkg");
        File.WriteAllBytes(zpkg, new byte[] { 0x00, 0x01, 0x02, 0x03 });   // 不是合法 zpkg
        var sources = new[] { Path.Combine(_tmp, "A.z42") };
        File.WriteAllText(sources[0], "namespace A;");

        var probe = new IncrementalBuild().Probe(sources, _tmp, Path.Combine(_tmp, "cache"), zpkg);
        probe.CachedCount.Should().Be(0);
        probe.FreshFiles.Should().BeEquivalentTo(sources);
    }
}
