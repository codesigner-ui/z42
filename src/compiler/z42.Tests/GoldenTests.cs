using Z42.Core.Text;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Z42.Semantics.Codegen;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics.TypeCheck;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Pipeline;
using Z42.Project;

namespace Z42.Tests;

/// <summary>
/// Golden test runner.
///
/// Layout under src/runtime/tests/golden/:
///   &lt;category&gt;/&lt;name&gt;/
///     source.z42          — z42 source input
///     expected.zasm       — expected ZASM output (for codegen tests)
///     expected_output.txt — expected stdout (for run tests)
///     expected_error.txt  — expected diagnostic output (for error tests)
///     features.toml       — optional LanguageFeatures overrides
///
/// Test discovery: every subdirectory that contains source.z42 is a test case.
/// </summary>
public sealed class GoldenTests
{
    private static readonly string GoldenRoot = FindGoldenRoot();

    private static string FindGoldenRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "runtime", "tests", "golden");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "runtime", "tests", "golden"));
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented          = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // StdlibCallIndex loaded once from artifacts/z42/libs/ (if present).
    private static readonly StdlibCallIndex StdlibIndex = LoadStdlibIndex();
    private static readonly string? StdlibLibsDir = FindStdlibLibsDir();

    private static string? FindStdlibLibsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "z42", "libs");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static StdlibCallIndex LoadStdlibIndex()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "z42", "libs");
            if (Directory.Exists(candidate)) return PackageCompiler.BuildStdlibIndex([candidate]);
            dir = dir.Parent;
        }
        return StdlibCallIndex.Empty;
    }

    // ── Test discovery ─────────────────────────────────────────────────────

    public static IEnumerable<object[]> ParseTestCases() =>
        DiscoverCases(hasExpectedZasm: true);

    public static IEnumerable<object[]> ErrorTestCases() =>
        DiscoverCases(errorCategory: true);

    public static IEnumerable<object[]> RunTestCases() =>
        DiscoverCases(runCategory: true);

    private static IEnumerable<object[]> DiscoverCases(
        bool hasExpectedZasm = false, bool errorCategory = false, bool runCategory = false)
    {
        if (!Directory.Exists(GoldenRoot)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(GoldenRoot, "*", SearchOption.AllDirectories))
        {
            string sourceFile = Path.Combine(dir, "source.z42");
            if (!File.Exists(sourceFile)) continue;

            bool isErrorCase = dir.Contains(Path.DirectorySeparatorChar + "errors" + Path.DirectorySeparatorChar)
                            || Path.GetFileName(dir).StartsWith("error_");
            bool isRunCase   = dir.Contains(Path.DirectorySeparatorChar + "run" + Path.DirectorySeparatorChar);

            if (errorCategory != isErrorCase) continue;
            if (runCategory   != isRunCase)   continue;
            if (hasExpectedZasm && !File.Exists(Path.Combine(dir, "expected.zasm"))) continue;

            string name = Path.GetRelativePath(GoldenRoot, dir).Replace(Path.DirectorySeparatorChar, '/');
            yield return [name, dir];
        }
    }

    // ── Features loader ────────────────────────────────────────────────────

    private static LanguageFeatures LoadFeatures(string dir)
    {
        string tomlPath = Path.Combine(dir, "features.toml");
        if (!File.Exists(tomlPath)) return LanguageFeatures.Phase1;

        var overrides = new Dictionary<string, bool>();
        foreach (var raw in File.ReadAllLines(tomlPath))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith('[')) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var val = line[(eq + 1)..].Trim().ToLowerInvariant();
            if (bool.TryParse(val, out var b)) overrides[key] = b;
        }
        return LanguageFeatures.Phase1.WithOverrides(overrides);
    }

    // ── Compile helper ─────────────────────────────────────────────────────

    private static (IrModule? ir, DiagnosticBag diags, IReadOnlySet<string> usedStdlibNs) Compile(string dir)
    {
        string sourceFile = Path.Combine(dir, "source.z42");
        string source     = File.ReadAllText(sourceFile);
        var features      = LoadFeatures(dir);
        var diags         = new DiagnosticBag();

        var tokens = new Lexer(source, sourceFile).Tokenize();

        var parser = new Parser(tokens, features);
        var cu     = parser.ParseCompilationUnit();
        foreach (var d in parser.Diagnostics.All) diags.Add(d);
        if (diags.HasErrors) return (null, diags, new HashSet<string>());

        var typeChecker = new TypeChecker(diags, features);
        var sem = typeChecker.Check(cu);
        if (diags.HasErrors) return (null, diags, new HashSet<string>());

        IrGen gen;
        IrModule ir;
        try
        {
            gen = new IrGen(StdlibIndex, features, sem);
            ir = gen.Generate(cu);
        }
        catch (Exception ex)
        {
            diags.Error(DiagnosticCodes.UnsupportedSyntax, ex.Message, new Span(0, 0, 0, 0, sourceFile));
            return (null, diags, new HashSet<string>());
        }

        return (ir, diags, gen.UsedStdlibNamespaces);
    }

    // ── IR / codegen tests (compare ZASM output) ──────────────────────────

    [Theory]
    [MemberData(nameof(ParseTestCases))]
    public void IrMatchesExpected(string name, string dir)
    {
        var (ir, diags, _) = Compile(dir);

        diags.PrintAll();
        diags.HasErrors.Should().BeFalse(because: $"test '{name}' should compile without errors");
        ir.Should().NotBeNull();

        string expectedZasm = File.ReadAllText(Path.Combine(dir, "expected.zasm")).Trim();
        string actualZasm   = ZasmWriter.Write(ir!).Trim();

        Assert.Equal(expectedZasm, actualZasm);
    }

    // ── Error / diagnostic tests ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ErrorTestCases))]
    public void DiagnosticsMatchExpected(string name, string dir)
    {
        var (_, diags, _) = Compile(dir);

        string expectedFile = Path.Combine(dir, "expected_error.txt");
        string expected     = File.ReadAllText(expectedFile).Trim();

        var sb = new StringBuilder();
        foreach (var d in diags.All) sb.AppendLine(d.ToString());
        string actual = sb.ToString().Trim();

        MatchDiagnosticLines(expected, actual, name);
    }

    // ── Run / execution tests ──────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(RunTestCases))]
    public void RunMatchesExpected(string name, string dir)
    {
        var (ir, diags, usedStdlibNs) = Compile(dir);
        diags.PrintAll();
        diags.HasErrors.Should().BeFalse(because: $"test '{name}' should compile without errors");
        ir.Should().NotBeNull();

        // Determine emit format
        string emitFmt = "zbc";
        string fmtFile = Path.Combine(dir, "emit_format.txt");
        if (File.Exists(fmtFile))
            emitFmt = File.ReadAllText(fmtFile).Trim().ToLowerInvariant();

        var zbc = new ZbcFile(
            ZbcVersion : ZbcFile.CurrentVersion,
            SourceFile : Path.Combine(dir, "source.z42"),
            SourceHash : "sha256:test",
            Namespace  : ir!.Name,
            Exports    : ir.Functions.Select(f => f.Name).ToList(),
            Imports    : usedStdlibNs.ToList(),
            Module     : ir
        );

        // Write binary artifact to temp file
        byte[] artifactBytes;
        string ext;
        if (emitFmt == "zpkg")
        {
            var pkgExports = ir.Functions
                .Select(f => new ZpkgExport($"{ir.Name}.{f.Name}", "func"))
                .ToList();
            var zpkg = new ZpkgFile(
                Name:         ir.Name,
                Version:      "0.1.0",
                Kind:         ZpkgKind.Exe,
                Mode:         ZpkgMode.Packed,
                Namespaces:   [ir.Name],
                Exports:      pkgExports,
                Dependencies: usedStdlibNs.Select(ns => new ZpkgDep($"{ns}.zpkg", [ns])).ToList(),
                Files:        [],
                Modules:      [zbc],
                Entry:        $"{ir.Name}.Main"
            );
            artifactBytes = ZpkgWriter.Write(zpkg);
            ext = ".zpkg";
        }
        else
        {
            artifactBytes = ZbcWriter.Write(ir, exports: ir.Functions.Select(f => f.Name));
            ext = ".zbc";
        }

        string irFile = Path.Combine(Path.GetTempPath(), $"z42_run_{Path.GetRandomFileName()}{ext}");
        File.WriteAllBytes(irFile, artifactBytes);

        try
        {
            string? vmBin = FindVmBinary();
            if (vmBin == null) return; // VM not built: skip

            var psi = new ProcessStartInfo(vmBin, irFile)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            if (StdlibLibsDir != null)
                psi.Environment["Z42_LIBS"] = StdlibLibsDir;

            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            proc.ExitCode.Should().Be(0,
                because: $"test '{name}' VM should exit cleanly (stderr: {proc.StandardError.ReadToEnd()})");

            string expectedFile = Path.Combine(dir, "expected_output.txt");
            if (File.Exists(expectedFile))
            {
                string expected = File.ReadAllText(expectedFile).ReplaceLineEndings("\n").Trim();
                string actual   = stdout.ReplaceLineEndings("\n").Trim();
                Assert.Equal(expected, actual);
            }
        }
        finally
        {
            File.Delete(irFile);
        }
    }

    private static string? FindVmBinary()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "rust", "debug", "z42vm");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static void MatchDiagnosticLines(string expected, string actual, string name)
    {
        var expLines = expected.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var actLines = actual.Split('\n',   StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        actLines.Should().HaveCount(expLines.Length,
            because: $"test '{name}' should produce exactly {expLines.Length} diagnostic(s)");

        for (int i = 0; i < expLines.Length; i++)
        {
            if (expLines[i].Contains('*'))
            {
                var parts = expLines[i].Split('*');
                foreach (var part in parts.Where(p => p.Length > 0))
                    actLines[i].Should().Contain(part,
                        because: $"test '{name}' diagnostic[{i}] should contain '{part}'");
            }
            else
            {
                actLines[i].Should().Be(expLines[i],
                    because: $"test '{name}' diagnostic[{i}] should match exactly");
            }
        }
    }
}
