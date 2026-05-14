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

namespace Z42.Tests.Zpkg;

/// <summary>
/// Byte-level + JSON-level golden harness for fixtures under <c>src/tests/zpkg-format/</c>.
/// Spec: <c>docs/spec/archive/&lt;date&gt;-freeze-zpkg-v0/</c>.
///
/// Each fixture exercises a distinct zpkg wire layout (packed-minimal /
/// packed-multi-module / indexed-minimal / sym-only-sidecar). Set
/// <c>Z42_ZPKG_REGEN=1</c> on the env to regenerate fixtures in-place
/// rather than asserting (the role of <c>generate-fixtures.sh</c>).
/// </summary>
public class FormatGoldenTests
{
    private static readonly DependencyIndex DepIndex = LoadDepIndex();
    private static readonly ImportedSymbols? Imported = LoadImported();
    private static readonly string FixtureRoot = FindFixtureRoot();

    private static bool RegenMode =>
        Environment.GetEnvironmentVariable("Z42_ZPKG_REGEN") == "1";

    public static IEnumerable<object[]> AllFixtures() =>
        new[] { "packed-minimal", "packed-multi-module", "indexed-minimal", "sym-only-sidecar" }
            .Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void ByteEqual(string fixture)
    {
        byte[] expected = ReadOrRegenFixtureBytes(fixture);
        byte[] actual = BuildFixture(fixture);

        actual.Should().Equal(expected,
            because: $"fixture `{fixture}` byte-level mismatch — wire format drifted. " +
                     $"If intentional, run with Z42_ZPKG_REGEN=1 and commit the diff.");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void JsonEqual(string fixture)
    {
        string expected = ReadOrRegenFixtureJson(fixture);
        byte[] zpkgBytes = BuildFixture(fixture);
        string actual = ZpkgGoldenJsonFormatter.Format(zpkgBytes);

        actual.Replace("\r\n", "\n").Should().Be(expected.Replace("\r\n", "\n"),
            because: $"fixture `{fixture}` JSON-level mismatch — section / metadata shape drifted. " +
                     $"If intentional, run with Z42_ZPKG_REGEN=1 and commit.");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void WriterDeterministic(string fixture)
    {
        byte[] first = BuildFixture(fixture);
        byte[] second = BuildFixture(fixture);

        second.Should().Equal(first,
            because: $"fixture `{fixture}`: ZpkgWriter not deterministic — likely hash-set / dict iteration leak");
    }

    // ── Fixture build dispatch ─────────────────────────────────────────────

    private static byte[] BuildFixture(string fixture)
    {
        return fixture switch
        {
            "packed-minimal"       => BuildPackedSingleFile("packed-minimal",      "demo.minimal",  ZpkgKind.Lib),
            "packed-multi-module"  => BuildPackedMultiModule(),
            "indexed-minimal"      => BuildIndexedSingleFile(),
            "sym-only-sidecar"     => BuildSymOnlySidecar(),
            _ => throw new InvalidOperationException($"unknown fixture: {fixture}"),
        };
    }

    private static byte[] BuildPackedSingleFile(string fixture, string pkgName, ZpkgKind kind)
    {
        string srcPath = Path.Combine(FixtureRoot, fixture, "source.z42");
        var zbcFile = CompileToZbcFile(srcPath);
        var zpkg = ZpkgBuilder.BuildPacked(
            name: pkgName,
            version: "0.1.0",
            kind: kind,
            entry: null,
            zbcFiles: [zbcFile],
            dependencies: []);
        return ZpkgWriter.Write(zpkg);
    }

    private static byte[] BuildPackedMultiModule()
    {
        string dir = Path.Combine(FixtureRoot, "packed-multi-module");
        var zbcA = CompileToZbcFile(Path.Combine(dir, "mod_a.z42"));
        var zbcB = CompileToZbcFile(Path.Combine(dir, "mod_b.z42"));
        var zpkg = ZpkgBuilder.BuildPacked(
            name: "demo.multi",
            version: "0.1.0",
            kind: ZpkgKind.Lib,
            entry: null,
            zbcFiles: [zbcA, zbcB],
            dependencies: []);
        return ZpkgWriter.Write(zpkg);
    }

    private static byte[] BuildIndexedSingleFile()
    {
        // For indexed mode, we don't write zbc files to a cache dir (would
        // require disk I/O); construct a minimal ZpkgFile directly with one
        // FileEntry referencing a synthetic relative path. The goal is to
        // exercise the indexed-mode wire layout, not full incremental build.
        string srcPath = Path.Combine(FixtureRoot, "indexed-minimal", "source.z42");
        var zbcFile = CompileToZbcFile(srcPath);
        var zpkg = new ZpkgFile(
            Name:         "demo.indexed",
            Version:      "0.1.0",
            Kind:         ZpkgKind.Lib,
            Mode:         ZpkgMode.Indexed,
            Namespaces:   [zbcFile.Namespace],
            Exports:      [],
            Dependencies: [],
            Files:        [new ZpkgFileEntry(
                Source: "source.z42",
                Bytecode: "source.zbc",
                SourceHash: zbcFile.SourceHash,
                Exports: [])],
            Modules:      [],
            Entry:        null);
        return ZpkgWriter.Write(zpkg, [zbcFile]);
    }

    private static byte[] BuildSymOnlySidecar()
    {
        // Build a packed zpkg with stripSymbols=true and return ONLY the
        // sidecar bytes (the .zsym half of the strip output).
        string srcPath = Path.Combine(FixtureRoot, "sym-only-sidecar", "source.z42");
        var zbcFile = CompileToZbcFile(srcPath);
        var zpkg = ZpkgBuilder.BuildPacked(
            name: "demo.sidecar",
            version: "0.1.0",
            kind: ZpkgKind.Lib,
            entry: null,
            zbcFiles: [zbcFile],
            dependencies: []);
        var (_, sidecar) = ZpkgWriter.WritePackedWithSidecar(zpkg, stripSymbols: true);
        if (sidecar is null)
            throw new InvalidOperationException("WritePackedWithSidecar returned null sidecar");
        return sidecar;
    }

    // ── Fixture I/O (regen vs assert) ──────────────────────────────────────

    private static byte[] ReadOrRegenFixtureBytes(string fixture)
    {
        string path = Path.Combine(FixtureRoot, fixture, "source.zpkg");
        if (RegenMode)
        {
            byte[] bytes = BuildFixture(fixture);
            File.WriteAllBytes(path, bytes);
            return bytes;
        }
        File.Exists(path).Should().BeTrue($"fixture `{fixture}` missing source.zpkg — run with Z42_ZPKG_REGEN=1");
        return File.ReadAllBytes(path);
    }

    private static string ReadOrRegenFixtureJson(string fixture)
    {
        string path = Path.Combine(FixtureRoot, fixture, "expected.json");
        if (RegenMode)
        {
            byte[] zpkgBytes = BuildFixture(fixture);
            string json = ZpkgGoldenJsonFormatter.Format(zpkgBytes);
            File.WriteAllText(path, json);
            return json;
        }
        File.Exists(path).Should().BeTrue($"fixture `{fixture}` missing expected.json — run with Z42_ZPKG_REGEN=1");
        return File.ReadAllText(path);
    }

    // ── Compile helpers ────────────────────────────────────────────────────

    private static ZbcFile CompileToZbcFile(string sourcePath)
    {
        string source = File.ReadAllText(sourcePath);
        IrModule module = CompileSource(source);
        return new ZbcFile(
            ZbcVersion: ZbcFile.CurrentVersion,
            SourceFile: Path.GetFileName(sourcePath),
            SourceHash: Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source))),
            Namespace:  module.Name,
            Exports:    [],
            Imports:    [],
            Module:     module);
    }

    private static IrModule CompileSource(string source)
    {
        var tokens = new Lexer(source, "<fixture>").Tokenize();
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

    // ── Workspace discovery (mirrors zbc-format harness) ───────────────────

    private static string FindFixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "tests", "zpkg-format");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return string.Empty;
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
