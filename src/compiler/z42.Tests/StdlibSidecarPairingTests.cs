using FluentAssertions;
using Xunit;
using Z42.IR.BinaryFormat;
using Z42.Project;

namespace Z42.Tests;

/// <summary>
/// End-to-end gate: every stdlib zpkg under <c>artifacts/libraries/&lt;name&gt;/dist/</c>
/// must have a matching <c>.zsym</c> sidecar whose BLID byte-equals the main zpkg's BLID.
/// Sequential collection: must not overlap with IncrementalBuildIntegrationTests which
/// deletes and rebuilds the same artifacts directory.
/// This is the strongest "the whole pipeline ships consistently" check —
/// covers ZbcWriter, ZpkgWriter, ZpkgBuilder.WriteZpkgWithSidecar, and the
/// driver's `--release` strip wiring in one shot.
/// </summary>
[Collection("StdlibArtifacts")]
public sealed class StdlibSidecarPairingTests
{
    static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "examples")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "src", "compiler")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("repo root not found");
        }
    }

    [Fact]
    public void EveryStdlibZpkgHasMatchingSidecar()
    {
        string distRoot = Path.Combine(RepoRoot, "artifacts", "build", "libraries");
        if (!Directory.Exists(distRoot))
            return; // stdlib hasn't been built in this clone — nothing to verify, treat as skip
        var zpkgPaths = Directory.EnumerateFiles(distRoot, "*.zpkg", SearchOption.AllDirectories)
            .Where(p => p.Contains("/dist/"))
            .ToList();

        if (zpkgPaths.Count == 0)
            return; // no built artifacts; skip silently

        var failures = new List<string>();
        foreach (var zpkg in zpkgPaths)
        {
            string zsym = Path.ChangeExtension(zpkg, ".zsym");
            if (!File.Exists(zsym))
            {
                failures.Add($"missing sidecar: {Path.GetRelativePath(RepoRoot, zsym)}");
                continue;
            }

            byte[] mainBytes = File.ReadAllBytes(zpkg);
            byte[] symBytes  = File.ReadAllBytes(zsym);
            byte[]? mainBlid = ZpkgReader.ReadBuildId(mainBytes);
            byte[]? symBlid  = ZpkgReader.ReadBuildId(symBytes);

            if (mainBlid is null)
            {
                failures.Add($"main zpkg has no BLID: {Path.GetRelativePath(RepoRoot, zpkg)}");
                continue;
            }
            if (symBlid is null)
            {
                failures.Add($"sidecar has no BLID: {Path.GetRelativePath(RepoRoot, zsym)}");
                continue;
            }
            if (!mainBlid.AsSpan().SequenceEqual(symBlid))
            {
                failures.Add(
                    $"BLID mismatch for {Path.GetFileNameWithoutExtension(zpkg)}: " +
                    $"zpkg={BuildId.ShortHex(mainBlid)}… zsym={BuildId.ShortHex(symBlid)}…");
            }
        }

        failures.Should().BeEmpty(
            "every stdlib zpkg in artifacts/libraries/*/dist/ must have a build_id-matched .zsym sidecar after release build");
    }

    [Fact]
    public void StdlibSidecarRoundTripsLineTable()
    {
        // Pick one stdlib zpkg, load it via ReadModules, then apply its sidecar
        // via ApplyDebugInfo. Verify that at least one function ends up with a
        // populated LineTable (proves the sidecar actually carries real debug
        // data, not an empty MDBG section).
        string distRoot = Path.Combine(RepoRoot, "artifacts", "build", "libraries");
        if (!Directory.Exists(distRoot)) return;

        string? targetZpkg = Directory.EnumerateFiles(distRoot, "z42.core.zpkg", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Contains("/dist/"));
        if (targetZpkg is null) return;

        string targetZsym = Path.ChangeExtension(targetZpkg, ".zsym");
        if (!File.Exists(targetZsym)) return;

        byte[] mainBytes = File.ReadAllBytes(targetZpkg);
        byte[] symBytes  = File.ReadAllBytes(targetZsym);

        var mainModules = ZpkgReader.ReadModules(mainBytes);
        mainModules.Should().NotBeEmpty();

        var sidecar = ZpkgReader.ReadSidecar(symBytes);
        var merged  = ZpkgReader.ApplyDebugInfo(mainModules, mainBytes, sidecar);

        int linesPopulated = merged
            .SelectMany(m => m.Module.Functions)
            .Count(f => f.LineTable is { Count: > 0 });
        linesPopulated.Should().BeGreaterThan(0,
            "z42.core stdlib has real source lines; sidecar must carry at least one populated LineTable after merge");
    }
}
