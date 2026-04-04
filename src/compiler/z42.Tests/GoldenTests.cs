using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Z42.Compiler.Codegen;
using Z42.Compiler.Diagnostics;
using Z42.Compiler.Features;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;
using Z42.Compiler.TypeCheck;
using Z42.IR;
using Z42.Project;

namespace Z42.Tests;

/// <summary>
/// Golden test runner.
///
/// Layout under src/runtime/tests/golden/:
///   &lt;category&gt;/&lt;name&gt;/
///     source.z42          — z42 source input
///     expected.txt        — expected stdout (for run tests)
///     expected_ir.json    — expected IR JSON (optional, for codegen tests)
///     expected_error.txt  — expected diagnostic output (for error tests)
///     features.toml       — optional LanguageFeatures overrides
///
/// Test discovery: every subdirectory that contains source.z42 is a test case.
/// Category is inferred from the directory name prefix (errors/ → error test).
/// </summary>
public sealed class GoldenTests
{
    private static readonly string GoldenRoot = FindGoldenRoot();

    private static string FindGoldenRoot()
    {
        // Walk up from the test binary until we find the repo root (contains src/runtime/tests/golden)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "runtime", "tests", "golden");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        // Fallback: relative from source (dev run)
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

    // ── Test discovery ─────────────────────────────────────────────────────

    public static IEnumerable<object[]> ParseTestCases() =>
        DiscoverCases(hasExpectedIr: true);

    public static IEnumerable<object[]> ErrorTestCases() =>
        DiscoverCases(errorCategory: true);

    public static IEnumerable<object[]> RunTestCases() =>
        DiscoverCases(runCategory: true);

    private static IEnumerable<object[]> DiscoverCases(
        bool hasExpectedIr = false, bool errorCategory = false, bool runCategory = false)
    {
        if (!Directory.Exists(GoldenRoot))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(GoldenRoot, "*", SearchOption.AllDirectories))
        {
            string sourceFile = Path.Combine(dir, "source.z42");
            if (!File.Exists(sourceFile)) continue;

            bool isErrorCase = dir.Contains(Path.DirectorySeparatorChar + "errors" + Path.DirectorySeparatorChar)
                            || Path.GetFileName(dir).StartsWith("error_");
            bool isRunCase   = dir.Contains(Path.DirectorySeparatorChar + "run" + Path.DirectorySeparatorChar);

            if (errorCategory != isErrorCase) continue;
            if (runCategory   != isRunCase)   continue;
            if (hasExpectedIr && !File.Exists(Path.Combine(dir, "expected_ir.json"))) continue;
            // expected_output.txt is optional: if absent, only the exit code is checked (Assert-based tests)

            string name = Path.GetRelativePath(GoldenRoot, dir).Replace(Path.DirectorySeparatorChar, '/');
            yield return [name, dir];
        }
    }

    // ── Features loader ────────────────────────────────────────────────────

    /// Parses a minimal key=value TOML (features.toml) to override LanguageFeatures.
    /// Lines starting with # or [ are ignored. Values must be true or false.
    private static LanguageFeatures LoadFeatures(string dir)
    {
        string tomlPath = Path.Combine(dir, "features.toml");
        if (!File.Exists(tomlPath))
            return LanguageFeatures.Phase1;

        var overrides = new Dictionary<string, bool>();
        foreach (var raw in File.ReadAllLines(tomlPath))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith('['))
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var val = line[(eq + 1)..].Trim().ToLowerInvariant();
            if (bool.TryParse(val, out var b))
                overrides[key] = b;
        }
        return LanguageFeatures.Phase1.WithOverrides(overrides);
    }

    // ── Compile helper ─────────────────────────────────────────────────────

    private static (IrModule? ir, DiagnosticBag diags) Compile(string dir)
    {
        string sourceFile = Path.Combine(dir, "source.z42");
        string source     = File.ReadAllText(sourceFile);

        // Load feature overrides if present (features.toml: key = true/false lines)
        var features = LoadFeatures(dir);

        var diags = new DiagnosticBag();

        var lexer  = new Lexer(source, sourceFile);
        var tokens = lexer.Tokenize();

        CompilationUnit cu;
        try
        {
            var parser = new Parser(tokens, features);
            cu = parser.ParseCompilationUnit();
        }
        catch (ParseException ex)
        {
            diags.Error(DiagnosticCodes.UnexpectedToken, ex.Message, ex.Span);
            return (null, diags);
        }

        var typeChecker = new TypeChecker(diags);
        typeChecker.Check(cu);
        if (diags.HasErrors) return (null, diags);

        IrModule ir;
        try
        {
            ir = new IrGen().Generate(cu);
        }
        catch (Exception ex)
        {
            diags.Error(DiagnosticCodes.UnsupportedSyntax, ex.Message,
                        new Span(0, 0, 0, 0, sourceFile));
            return (null, diags);
        }

        return (ir, diags);
    }

    // ── IR / codegen tests ─────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ParseTestCases))]
    public void IrMatchesExpected(string name, string dir)
    {
        var (ir, diags) = Compile(dir);

        diags.PrintAll();
        diags.HasErrors.Should().BeFalse(
            because: $"test '{name}' should compile without errors");
        ir.Should().NotBeNull();

        string expectedJson = File.ReadAllText(Path.Combine(dir, "expected_ir.json")).Trim();
        string actualJson   = JsonSerializer.Serialize(ir, JsonOpts).Trim();

        // Use xUnit Assert.Equal to avoid FluentAssertions crashing on {} in JSON strings
        Assert.Equal(expectedJson, actualJson);
    }

    // ── Error / diagnostic tests ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ErrorTestCases))]
    public void DiagnosticsMatchExpected(string name, string dir)
    {
        var (_, diags) = Compile(dir);

        string expectedFile = Path.Combine(dir, "expected_error.txt");
        string expected     = File.ReadAllText(expectedFile).Trim();

        var sb = new StringBuilder();
        foreach (var d in diags.All)
            sb.AppendLine(d.ToString());
        string actual = sb.ToString().Trim();

        // Match line-by-line, ignoring exact column in spans where marked with *
        MatchDiagnosticLines(expected, actual, name);
    }

    // ── Run / execution tests ──────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(RunTestCases))]
    public void RunMatchesExpected(string name, string dir)
    {
        // Step 1 — compile to IR JSON
        var (ir, diags) = Compile(dir);
        diags.PrintAll();
        diags.HasErrors.Should().BeFalse(because: $"test '{name}' should compile without errors");
        ir.Should().NotBeNull();

        // Step 2 — serialise artifact in the format requested by the test
        //   Default: .zbc   (single-file bytecode, production path)
        //   Override: emit_format.txt containing "zpkg" uses .zpkg packed bundle format
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
            Imports    : [],
            Module     : ir
        );

        string artifactJson;
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
                Dependencies: [],
                Files:        [],
                Modules:      [zbc],
                Entry:        $"{ir.Name}.Main"
            );
            artifactJson = JsonSerializer.Serialize(zpkg, JsonOpts);
            ext = ".zpkg";
        }
        else
        {
            artifactJson = JsonSerializer.Serialize(zbc, JsonOpts);
            ext = ".zbc";
        }

        string irFile = Path.Combine(Path.GetTempPath(), $"z42_run_{Path.GetRandomFileName()}{ext}");
        File.WriteAllText(irFile, artifactJson);

        try
        {
            // Step 3 — find the VM binary (built via `cargo build`)
            string? vmBin = FindVmBinary();
            if (vmBin == null)
            {
                // VM not built: skip the test rather than fail
                return;
            }

            // Step 4 — execute and capture stdout
            var psi = new ProcessStartInfo(vmBin, irFile)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            proc.ExitCode.Should().Be(0,
                because: $"test '{name}' VM should exit cleanly (stderr: {proc.StandardError.ReadToEnd()})");

            // Step 5 — compare output (if expected_output.txt present; otherwise exit code 0 suffices)
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
        // Walk up from test binary to repo root, look for artifacts/rust/debug/z42vm
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
            // Lines that contain "*" as a wildcard segment are matched with Contains
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
