using System.Text.Json;
using System.Text.Json.Serialization;
using Z42.Build;
using Z42.Compiler.Codegen;
using Z42.Compiler.Diagnostics;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;
using Z42.Compiler.TypeCheck;
using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Driver;

static class BuildCommand
{
    public static int Run(string[] args, JsonSerializerOptions jsonOptions)
    {
        // ── Parse build flags ─────────────────────────────────────────────────
        bool    useRelease   = false;
        string? emitOverride = null;
        string? explicitToml = null;
        string? exeFilter    = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--release":
                    useRelease = true;
                    break;
                case "--emit" when i + 1 < args.Length:
                    emitOverride = args[++i];
                    break;
                case "--exe" when i + 1 < args.Length:
                    exeFilter = args[++i];
                    break;
                default:
                    if (args[i].EndsWith(".z42.toml", StringComparison.OrdinalIgnoreCase))
                        explicitToml = args[i];
                    break;
            }
        }

        // ── Discover & load manifest ──────────────────────────────────────────
        string tomlPath;
        ProjectManifest manifest;
        try
        {
            tomlPath = ProjectManifest.Discover(Directory.GetCurrentDirectory(), explicitToml);
            manifest = ProjectManifest.Load(tomlPath);
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        string profileLabel = useRelease ? "release" : "debug";
        string emit         = emitOverride ?? manifest.Build.Emit;
        string projectDir   = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;
        string outDir       = Path.GetFullPath(Path.Combine(projectDir, manifest.Build.OutDir));

        // ── Route: multi-exe vs single-target ────────────────────────────────
        if (manifest.Project.Kind == Z42.Build.ProjectKind.Multi)
            return BuildMultiExe(manifest, projectDir, outDir, emit, profileLabel, exeFilter, jsonOptions);

        // ── Single-target build ───────────────────────────────────────────────
        if (exeFilter is not null)
        {
            Console.Error.WriteLine(
                "error: --exe flag is only valid for projects with [[exe]] targets");
            return 1;
        }

        IReadOnlyList<string> sourceFiles;
        try   { sourceFiles = manifest.ResolveSourceFiles(projectDir); }
        catch (ManifestException ex) { Console.Error.WriteLine(ex.Message); return 1; }

        Console.Error.WriteLine(
            $"Building {manifest.Project.Name} v{manifest.Project.Version} " +
            $"[{profileLabel}] ({sourceFiles.Count} file(s))");

        int errors = 0;
        foreach (var sourceFile in sourceFiles)
        {
            if (CompileSingleFile(sourceFile, emit, outDir, manifest, jsonOptions) != 0)
                errors++;
        }

        if (errors > 0) { Console.Error.WriteLine($"error: build failed ({errors} file(s) with errors)"); return 1; }
        Console.Error.WriteLine($"Build succeeded → {outDir}");
        return 0;
    }

    static int BuildMultiExe(
        ProjectManifest manifest,
        string projectDir,
        string outDir,
        string emit,
        string profileLabel,
        string? exeFilter,
        JsonSerializerOptions jsonOptions)
    {
        var targets = manifest.ExeTargets;

        // --exe filter
        if (exeFilter is not null)
        {
            targets = targets.Where(t => t.Name == exeFilter).ToList();
            if (targets.Count == 0)
            {
                Console.Error.WriteLine($"error: no [[exe]] named '{exeFilter}'");
                return 1;
            }
        }

        Console.Error.WriteLine(
            $"Building {manifest.Project.Name} v{manifest.Project.Version} " +
            $"[{profileLabel}] ({targets.Count} target(s))");

        int errors = 0;
        foreach (var target in targets)
        {
            Console.Error.WriteLine($"  Compiling {target.Name} ({target.Entry})");

            IReadOnlyList<string> sourceFiles;
            try   { sourceFiles = manifest.ResolveSourceFiles(projectDir, target); }
            catch (ManifestException ex) { Console.Error.WriteLine(ex.Message); errors++; continue; }

            // Each exe target gets its own output file: dist/<name>.zbc
            string targetOutDir = outDir;
            string targetEmit   = emit;

            foreach (var sourceFile in sourceFiles)
            {
                if (CompileSingleFile(sourceFile, targetEmit, targetOutDir, manifest, jsonOptions, target.Name) != 0)
                    errors++;
            }
        }

        if (errors > 0) { Console.Error.WriteLine($"error: build failed ({errors} error(s))"); return 1; }
        Console.Error.WriteLine($"Build succeeded → {outDir}");
        return 0;
    }

    // ── Single-file compilation (shared with single-file mode) ─────────────────

    public static int CompileSingleFile(
        string sourceFile,
        string emitMode,
        string? outDir,
        ProjectManifest? manifest,
        JsonSerializerOptions jsonOptions,
        string? outputBaseName = null)   // override output filename (used for [[exe]] targets)
    {
        string source;
        try   { source = File.ReadAllText(sourceFile); }
        catch { Console.Error.WriteLine($"error: cannot read {sourceFile}"); return 1; }

        // Lex
        var lexer  = new Lexer(source, sourceFile);
        var tokens = lexer.Tokenize();

        // Parse
        CompilationUnit cu;
        try   { cu = new Parser(tokens).ParseCompilationUnit(); }
        catch (ParseException ex)
        {
            Console.Error.WriteLine(
                $"parse error at {ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
            return 1;
        }

        // TypeCheck
        var diags = new DiagnosticBag();
        new TypeChecker(diags).Check(cu);
        if (diags.PrintAll()) return 1;

        // Codegen
        IrModule irModule;
        try   { irModule = new IrGen().Generate(cu); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"codegen error: {ex.Message}");
            return 1;
        }

        // Emit
        string ns         = cu.Namespace ?? "main";
        string sourceHash = SingleFileDriver.Sha256Hex(source);
        var    exports    = irModule.Functions.Select(f => f.Name).ToList();
        string baseName   = outputBaseName ?? Path.GetFileNameWithoutExtension(sourceFile);

        string resolvedOutDir = outDir
            ?? Path.GetDirectoryName(sourceFile)
            ?? ".";
        Directory.CreateDirectory(resolvedOutDir);

        switch (emitMode)
        {
            case "ir":
            {
                string path = Path.Combine(resolvedOutDir, baseName + ".z42ir.json");
                WriteFile(path, JsonSerializer.Serialize(irModule, jsonOptions));
                break;
            }
            case "zbc":
            {
                string path = Path.Combine(resolvedOutDir, baseName + ".zbc");
                File.WriteAllBytes(path, ZbcWriter.Write(irModule, exports));
                Console.Error.WriteLine($"wrote → {path}");
                break;
            }
            case "zasm":
            {
                string path = Path.Combine(resolvedOutDir, baseName + ".zasm");
                WriteFile(path, ZasmWriter.Write(irModule));
                break;
            }
            case "zmod":
            {
                string cacheDir = Path.Combine(resolvedOutDir, ".cache");
                string zbcName  = baseName + ".zbc";
                string zbcPath  = Path.Combine(cacheDir, zbcName);
                string zbcRel   = Path.GetRelativePath(resolvedOutDir, zbcPath);

                var zbc = new ZbcFile(ZbcFile.CurrentVersion, sourceFile, sourceHash, ns, exports, [], irModule);
                WriteFile(zbcPath, JsonSerializer.Serialize(zbc, jsonOptions));

                var entry  = new ZmodFileEntry(sourceFile, zbcRel, sourceHash, exports);
                var zmod   = new ZmodManifest(ZmodManifest.CurrentVersion, ns, "0.1.0",
                                 ZmodKind.Exe, [entry], [], $"{ns}.Main");
                string zmodPath = Path.Combine(resolvedOutDir, baseName + ".zmod");
                WriteFile(zmodPath, JsonSerializer.Serialize(zmod, jsonOptions));
                break;
            }
            case "zbin":
            {
                var zbc        = new ZbcFile(ZbcFile.CurrentVersion, sourceFile, sourceHash, ns, exports, [], irModule);
                var binExports = exports.Select(e => new ZbinExport($"{ns}.{e}", "func")).ToList();
                var zbin       = new ZbinFile(ZbinFile.CurrentVersion, ns, "0.1.0",
                                     ZmodKind.Exe, binExports, [], [zbc], $"{ns}.Main");
                string path    = Path.Combine(resolvedOutDir, baseName + ".zbin");
                WriteFile(path, JsonSerializer.Serialize(zbin, jsonOptions));
                break;
            }
            default:
                Console.Error.WriteLine($"error: unknown emit mode '{emitMode}'");
                return 1;
        }

        return 0;
    }

    static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, content);
        Console.Error.WriteLine($"wrote → {path}");
    }
}
