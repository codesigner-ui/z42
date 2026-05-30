using FluentAssertions;
using Xunit;
using Z42.Core;
using Z42.Core.Diagnostics;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Pipeline;
using Z42.Project;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests.Zbc;

/// <summary>
/// Byte-level + JSON-level golden harness for fixtures under <c>src/tests/zbc-format/</c>.
/// Spec: <c>docs/spec/archive/&lt;date&gt;-freeze-zbc-v1/</c>.
///
/// Each fixture exercises a distinct zbc wire layout (empty / TIDX / FRCS /
/// cross-import token / etc). Regen fixtures via
/// <c>src/tests/zbc-format/generate-fixtures.sh</c> after legitimate format
/// changes (writer minor bump) — git diff then shows the actual layout delta.
/// </summary>
public class FormatGoldenTests
{
    private static readonly DependencyIndex DepIndex = LoadDepIndex();
    private static readonly ImportedSymbols? Imported = LoadImported();
    private static readonly string FixtureRoot = FindFixtureRoot();

    public static IEnumerable<object[]> AllFixtures() =>
        Directory.Exists(FixtureRoot)
            ? Directory.EnumerateDirectories(FixtureRoot)
                .Where(d => File.Exists(Path.Combine(d, "source.z42")))
                .Select(d => new object[] { Path.GetFileName(d)! })
                .OrderBy(o => (string)o[0])
            : [];

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void ByteEqual(string fixture)
    {
        var (sourceZ42, expectedZbc, _) = FixturePaths(fixture);
        File.Exists(expectedZbc).Should().BeTrue($"fixture `{fixture}` missing source.zbc — run generate-fixtures.sh");

        IrModule module = Compile(File.ReadAllText(sourceZ42), sourceZ42);
        byte[] actual = ZbcWriter.Write(module);
        byte[] expected = File.ReadAllBytes(expectedZbc);

        actual.Should().Equal(expected,
            because: $"fixture `{fixture}` byte-level mismatch — wire format drifted. " +
                     $"If intentional, run src/tests/zbc-format/generate-fixtures.sh and commit the diff.");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void JsonEqual(string fixture)
    {
        var (sourceZ42, _, expectedJson) = FixturePaths(fixture);
        File.Exists(expectedJson).Should().BeTrue($"fixture `{fixture}` missing expected.json — run generate-fixtures.sh");

        IrModule module = Compile(File.ReadAllText(sourceZ42), sourceZ42);
        byte[] bytes = ZbcWriter.Write(module);
        IrModule readBack = ZbcReader.Read(bytes);
        string actual = ZbcGoldenJsonFormatter.Format(bytes, readBack);
        string expected = File.ReadAllText(expectedJson);

        // Normalize CRLF → LF so check-in line endings don't fight golden output.
        actual.Replace("\r\n", "\n").Should().Be(expected.Replace("\r\n", "\n"),
            because: $"fixture `{fixture}` JSON-level mismatch — section / opcode / class shape drifted. " +
                     $"If intentional, regen via generate-fixtures.sh and commit.");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void WriterDeterministic(string fixture)
    {
        // Same IrModule → ZbcWriter twice must produce byte-identical output.
        // Protects against any new field whose hash / iteration order is non-deterministic.
        var (sourceZ42, _, _) = FixturePaths(fixture);

        IrModule module = Compile(File.ReadAllText(sourceZ42), sourceZ42);
        byte[] first = ZbcWriter.Write(module);
        byte[] second = ZbcWriter.Write(module);

        second.Should().Equal(first,
            because: $"fixture `{fixture}`: ZbcWriter not deterministic — likely hash-set / dict iteration leak");
    }

    // Note: `ReadWriteRoundTrip` (read bytes → IrModule → re-write → byte-equal)
    // was originally dropped here due to lossy `retType` / `IrFieldDesc.Type`
    // TypeTag encoding. Re-enabled 2026-05-27 in a dedicated harness
    // (`ReadWriteRoundTripTests.cs`) after `align-zbc-reader-writer-asymmetry`
    // (zbc 1.7) added u32 type_str_idx alongside the tag.

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (string z42, string zbc, string json) FixturePaths(string fixture)
    {
        string dir = Path.Combine(FixtureRoot, fixture);
        return (Path.Combine(dir, "source.z42"),
                Path.Combine(dir, "source.zbc"),
                Path.Combine(dir, "expected.json"));
    }

    private static string FindFixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "tests", "zbc-format");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return string.Empty;
    }

    private static IrModule Compile(string source, string sourcePath = "<fixture>")
    {
        // fix-line-entry-file-population (2026-05-31): pass the actual source
        // path to the Lexer so emitted LineEntry.file matches what the on-
        // disk fixture (regen via generate-fixtures.sh + z42c driver) records.
        // Pre-spec we hardcoded "<fixture>" — fine when LineEntry.file was
        // always null, breaks now that every entry carries the file string.
        var tokens = new Lexer(source, sourcePath).Tokenize();
        var diags = new DiagnosticBag();
        CompilationUnit cu;
        try { cu = new Parser(tokens).ParseCompilationUnit(); }
        catch (ParseException ex)
        {
            throw new InvalidOperationException(
                $"Parse error at {ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
        }
        var model = new TypeChecker(diags, depIndex: DepIndex).Check(cu, Imported);
        if (diags.HasErrors)
        {
            var sw = new StringWriter();
            diags.PrintAll(sw);
            throw new InvalidOperationException("Type errors:\n" + sw);
        }
        return new IrGen(DepIndex, semanticModel: model).Generate(cu);
    }

    private static DependencyIndex LoadDepIndex()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "build", "libs", "release");
            if (Directory.Exists(candidate)) return PackageCompiler.BuildDepIndex([candidate]);
            dir = dir.Parent;
        }
        return DependencyIndex.Empty;
    }

    private static ImportedSymbols? LoadImported()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "build", "libs", "release");
            if (Directory.Exists(candidate))
            {
                var cache = new TsigCache();
                foreach (var zpkg in Directory.EnumerateFiles(candidate, "*.zpkg"))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(zpkg);
                        foreach (var ns in ZpkgReader.ReadNamespaces(bytes))
                            cache.RegisterNamespace(ns, zpkg);
                    }
                    catch { }
                }
                var allPkgs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (_, pkg) in cache.AllPackages()) allPkgs.Add(pkg);
                var (modules, packageOf) = cache.LoadForPackages(allPkgs);
                if (modules.Count == 0) return null;
                return ImportedSymbolLoader.Load(modules, packageOf, allPkgs,
                    preludePackages: PreludePackages.Names);
            }
            dir = dir.Parent;
        }
        return null;
    }
}
