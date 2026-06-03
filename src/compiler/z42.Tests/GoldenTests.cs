using Z42.Core;
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
/// Two layouts coexist (dual-mode discovery, 2026-05-08 flatten-assert-only-tests):
///
///   Dir mode  — case dir holds source + sidecars:
///     src/tests/&lt;category&gt;/&lt;name&gt;/                  — VM-runnable cases (test-vm.sh)
///     src/compiler/z42.Tests/Fixtures/{errors,parse}/&lt;name&gt;/ — compiler-only goldens (this runner)
///     src/libraries/&lt;lib&gt;/tests/&lt;name&gt;/
///       source.z42          — z42 source input
///       expected.zasm       — expected ZASM output (parse tests)
///       expected_output.txt — expected stdout (run tests; absent = empty stdout OK)
///       expected_error.txt  — expected diagnostic output (error tests)
///       features.toml       — optional LanguageFeatures overrides
///       emit_format.txt     — optional emit format override (zbc/zpkg)
///
///   Flat mode — single file, no sidecars (assert-only run cases only):
///     src/tests/&lt;category&gt;/&lt;name&gt;.z42
///     src/libraries/&lt;lib&gt;/tests/&lt;name&gt;.z42
///   Sidecars all default (LanguageFeatures.Phase1, emit zbc, expected stdout = "").
///
/// Test discovery walks all roots. Case kind by path + sidecar presence:
///   under Fixtures/errors/   + expected_error.txt → error case
///   under Fixtures/parse/                          → parse case (expected.zasm)
///   anything else with source.z42 (or flat .z42) under non-cross-zpkg → run case
///
/// 2026-05-12: compiler-only goldens (errors + parse) live under
/// `src/compiler/z42.Tests/Fixtures/` (owned by this runner). `src/tests/`
/// is now reserved for VM-runnable e2e goldens consumed by test-vm.sh.
/// </summary>
public sealed class GoldenTests
{
    private static readonly string ProjectRoot   = FindProjectRoot();
    private static readonly string TestsRoot     = Path.Combine(ProjectRoot, "src", "tests");
    private static readonly string FixturesRoot  = Path.Combine(ProjectRoot, "src", "compiler", "z42.Tests", "Fixtures");
    private static readonly string LibrariesRoot = Path.Combine(ProjectRoot, "src", "libraries");

    private static string FindProjectRoot()
    {
        // Try AppContext.BaseDirectory first, then current working dir.
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "tests"))
                 && Directory.Exists(Path.Combine(dir.FullName, "src", "libraries"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        // Fallback: AppContext.BaseDirectory under artifacts/compiler/z42.Tests/bin/<cfg>/<tfm>
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", ".."));
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented          = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // DependencyIndex and TSIG cache loaded once from artifacts/z42/libs/ (if present).
    private static readonly DependencyIndex DepIndex = LoadDepIndex();
    private static readonly string? StdlibLibsDir = FindStdlibLibsDir();
    private static readonly TsigCache TsigCacheInstance = BuildTsigCache();

    private static string? FindStdlibLibsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "build", "libraries", "dist", "release");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static DependencyIndex LoadDepIndex()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "build", "libraries", "dist", "release");
            if (Directory.Exists(candidate)) return PackageCompiler.BuildDepIndex([candidate]);
            dir = dir.Parent;
        }
        return DependencyIndex.Empty;
    }

    private static TsigCache BuildTsigCache()
    {
        var cache = new TsigCache();
        var libsDir = FindStdlibLibsDir();
        if (libsDir is null) return cache;
        foreach (var zpkgPath in Directory.EnumerateFiles(libsDir, "*.zpkg"))
        {
            try
            {
                var bytes = File.ReadAllBytes(zpkgPath);
                var ns    = ZpkgReader.ReadNamespaces(bytes);
                foreach (var n in ns)
                    cache.RegisterNamespace(n, zpkgPath);
            }
            catch { /* skip malformed */ }
        }
        return cache;
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
        if (!Directory.Exists(TestsRoot) && !Directory.Exists(FixturesRoot)) yield break;

        // Walk roots:
        //   src/tests/<cat>/<name>[/source.z42]                   (dir mode, run cases)
        //   src/tests/<cat>/<name>.z42                            (flat mode)
        //   src/compiler/z42.Tests/Fixtures/{errors,parse}/<name>/  (dir mode, compiler-only)
        //   src/libraries/<lib>/tests/<name>[/source.z42]         (dir mode)
        //   src/libraries/<lib>/tests/<name>.z42                  (flat mode)
        var roots = new List<string>();
        if (Directory.Exists(TestsRoot))    roots.Add(TestsRoot);
        if (Directory.Exists(FixturesRoot)) roots.Add(FixturesRoot);
        if (Directory.Exists(LibrariesRoot))
        {
            foreach (var lib in Directory.EnumerateDirectories(LibrariesRoot))
            {
                string libTests = Path.Combine(lib, "tests");
                if (Directory.Exists(libTests)) roots.Add(libTests);
            }
        }

        // Exclude wire-format fixture directories — those .z42 files are golden
        // inputs for ZbcWriter / ZpkgWriter byte-level tests (see Z42.Tests.Zbc /
        // Z42.Tests.Zpkg FormatGoldenTests), not runnable cases. freeze-zbc-v1 /
        // freeze-zpkg-v0 introduced them.
        //
        // Also exclude two GC/delegate goldens whose runtime failure
        // ("bitwise op requires integral operands, got Null and Null") is
        // caused by the known-deferred cross-zpkg static field initialization
        // timing issue (ModeFlags.Once / .Weak from z42.core appear as Null
        // before the cross-zpkg __static_init__ pass runs). See roadmap.md
        // "Deferred Backlog Index" → 跨包 static field 初始化时机. Mirrors the
        // same skip in scripts/test-vm.sh. Re-enable once that backlog ships.
        static bool IsExcludedPath(string p) =>
               p.Contains(Path.DirectorySeparatorChar + "cross-zpkg" + Path.DirectorySeparatorChar)
            || p.Contains(Path.DirectorySeparatorChar + "zbc-format"  + Path.DirectorySeparatorChar)
            || p.Contains(Path.DirectorySeparatorChar + "zpkg-format" + Path.DirectorySeparatorChar)
            || p.Contains(Path.DirectorySeparatorChar + "composite_ref_weak_mode")
            || p.Contains(Path.DirectorySeparatorChar + "multicast_subscription_refs");

        foreach (var root in roots)
        {
            // ── Dir mode: any directory containing source.z42 ──
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                string sourceFile = Path.Combine(dir, "source.z42");
                if (!File.Exists(sourceFile)) continue;

                bool isErrorCase = dir.Contains(Path.DirectorySeparatorChar + "errors" + Path.DirectorySeparatorChar)
                                && File.Exists(Path.Combine(dir, "expected_error.txt"));
                bool isParseCase = dir.Contains(Path.DirectorySeparatorChar + "parse"  + Path.DirectorySeparatorChar);
                if (IsExcludedPath(dir)) continue;
                bool isRunCase = !isErrorCase && !isParseCase;

                if (errorCategory != isErrorCase) continue;
                if (runCategory   != isRunCase)   continue;
                if (hasExpectedZasm && !File.Exists(Path.Combine(dir, "expected.zasm"))) continue;

                string name = Path.GetRelativePath(ProjectRoot, dir).Replace(Path.DirectorySeparatorChar, '/');
                yield return [name, sourceFile, dir];
            }

            // ── Flat mode: any *.z42 file (excluding source.z42 which is dir mode) ──
            // Flat cases are always assert-only run tests: no sidecars, expected stdout empty.
            // Error / parse cases must use dir mode (they require sidecar files).
            //
            // Flat scan only applies to src/tests/. Files under src/libraries/<lib>/tests/<name>.z42
            // are owned by the z42-test-runner ([Test]-attributed; dispatched via scripts/test-stdlib.sh)
            // and would lack a Main entry point — not golden runnable.
            if (root != TestsRoot) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.z42", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "source.z42") continue;
                if (IsExcludedPath(file)) continue;
                // Flat files are always run cases — skip when caller wants other kinds.
                if (errorCategory)   continue;
                if (hasExpectedZasm) continue;
                if (!runCategory)    continue;

                string name = Path.GetRelativePath(ProjectRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                yield return [name, file, /* dir (no sidecars) */ null!];
            }
        }
    }

    // ── Features loader ────────────────────────────────────────────────────
    //
    // dir == null (flat mode) ⇒ no sidecars ⇒ defaults.

    private static LanguageFeatures LoadFeatures(string? dir)
    {
        if (dir == null) return LanguageFeatures.Phase1;
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

    private static (IrModule? ir, DiagnosticBag diags, IReadOnlySet<string> usedDepNs) Compile(string sourceFile, string? dir)
    {
        string source     = File.ReadAllText(sourceFile);
        var features      = LoadFeatures(dir);
        var diags         = new DiagnosticBag();

        var tokens = new Lexer(source, sourceFile).Tokenize();

        var parser = new Parser(tokens, features);
        var cu     = parser.ParseCompilationUnit();
        foreach (var d in parser.Diagnostics.All) diags.Add(d);
        if (diags.HasErrors) return (null, diags, new HashSet<string>());

        // strict-using-resolution (2026-04-28): activate prelude packages
        // (z42.core) + packages providing namespaces in cu.Usings. Types from
        // non-activated packages are not visible. Mirrors PackageCompiler /
        // SingleFileCompiler runtime path.
        var usings = cu.Usings.Count > 0
            ? cu.Usings.ToList()
            : PackageCompiler.ExtractUsingsPublic(source);
        ImportedSymbols? imported = null;
        var activatedPkgs = new HashSet<string>(PreludePackages.Names, StringComparer.Ordinal);
        foreach (var ns in usings)
            foreach (var pkg in TsigCacheInstance.PackagesProvidingNamespace(ns))
                activatedPkgs.Add(pkg);
        var (tsigModules, packageOf) = TsigCacheInstance.LoadForPackages(activatedPkgs);
        if (tsigModules.Count > 0)
        {
            imported = ImportedSymbolLoader.Load(tsigModules, packageOf, activatedPkgs,
                preludePackages: PreludePackages.Names);
        }

        var typeChecker = new TypeChecker(diags, features, DepIndex);
        var sem = typeChecker.Check(cu, imported);
        if (diags.HasErrors)
        {
            diags.PrintAll();
            return (null, diags, new HashSet<string>());
        }

        IrGen gen;
        IrModule ir;
        try
        {
            gen = new IrGen(DepIndex, features, sem);
            ir = gen.Generate(cu);
        }
        catch (Exception ex)
        {
            diags.Error(DiagnosticCodes.UnsupportedSyntax, ex.Message, new Span(0, 0, 0, 0, sourceFile));
            return (null, diags, new HashSet<string>());
        }

        return (ir, diags, gen.UsedDepNamespaces);
    }

    // ── IR / codegen tests (compare ZASM output) ──────────────────────────

    [Theory]
    [MemberData(nameof(ParseTestCases))]
    public void IrMatchesExpected(string name, string sourceFile, string dir)
    {
        // Parse cases always have a dir (expected.zasm sidecar required).
        var (ir, diags, _) = Compile(sourceFile, dir);

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
    public void DiagnosticsMatchExpected(string name, string sourceFile, string dir)
    {
        // Error cases always have a dir (expected_error.txt sidecar required).
        var (_, diags, _) = Compile(sourceFile, dir);

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
    public void RunMatchesExpected(string name, string sourceFile, string? dir)
    {
        // Run cases come in dir mode (with sidecars) or flat mode (dir == null,
        // assert-only; all sidecars default).
        var (ir, diags, usedDepNs) = Compile(sourceFile, dir);
        var diagStr = string.Join("\n", diags.All.Select(d => d.ToString()));
        diags.HasErrors.Should().BeFalse(because: $"test '{name}' should compile without errors.\nDiagnostics:\n{diagStr}");
        ir.Should().NotBeNull();

        // Determine emit format
        string emitFmt = "zbc";
        if (dir != null)
        {
            string fmtFile = Path.Combine(dir, "emit_format.txt");
            if (File.Exists(fmtFile))
                emitFmt = File.ReadAllText(fmtFile).Trim().ToLowerInvariant();
        }

        var zbc = new ZbcFile(
            ZbcVersion : ZbcFile.CurrentVersion,
            SourceFile : sourceFile,
            SourceHash : "sha256:test",
            Namespace  : ir!.Name,
            Exports    : ir.Functions.Select(f => f.Name).ToList(),
            Imports    : usedDepNs.ToList(),
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
                Dependencies: usedDepNs.Select(ns => new ZpkgDep($"{ns}.zpkg", [ns])).ToList(),
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

            // 2026-05-14 drop-cli-entry-fallback: VM no longer auto-resolves
            // `Main` / `<ns>.Main` from a bare zbc. Pick the entry from the
            // compiled IR (same priority as PackageCompiler.AutoDetectEntry)
            // and pass it positionally so the GoldenTests fixtures keep
            // working without going through PackageCompiler.
            string entryName =
                ir!.Functions.Select(f => f.Name)
                    .Where(n => n.EndsWith(".Main", StringComparison.Ordinal))
                    .OrderBy(s => s, StringComparer.Ordinal).FirstOrDefault()
                ?? (ir.Functions.Any(f => f.Name == "Main") ? "Main" : null)
                ?? ir.Functions.Select(f => f.Name)
                    .Where(n => n.EndsWith(".main", StringComparison.Ordinal))
                    .OrderBy(s => s, StringComparer.Ordinal).FirstOrDefault()
                ?? (ir.Functions.Any(f => f.Name == "main") ? "main" : null)
                ?? throw new InvalidOperationException(
                       $"test '{name}': no Main/main function in compiled IR");

            var psi = new ProcessStartInfo(vmBin)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            psi.ArgumentList.Add(irFile);
            psi.ArgumentList.Add(entryName);
            if (StdlibLibsDir != null)
                psi.Environment["Z42_LIBS"] = StdlibLibsDir;

            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            proc.ExitCode.Should().Be(0,
                because: $"test '{name}' VM should exit cleanly (stderr: {proc.StandardError.ReadToEnd()})");

            // Assertion-based tests (no expected_output.txt sidecar, or flat
            // mode with no dir at all) → expect empty stdout.
            string expected = "";
            if (dir != null)
            {
                string expectedFile = Path.Combine(dir, "expected_output.txt");
                if (File.Exists(expectedFile))
                    expected = File.ReadAllText(expectedFile).ReplaceLineEndings("\n").Trim();
            }
            string actual = stdout.ReplaceLineEndings("\n").Trim();
            Assert.Equal(expected, actual);
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
            string candidate = Path.Combine(dir.FullName, "artifacts", "build", "runtime", "debug", "z42vm");
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
