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
        bool   useRelease   = false;
        string? profileName = null;
        string? emitOverride = null;
        string? explicitToml = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--release":
                    useRelease = true;
                    break;
                case "--profile" when i + 1 < args.Length:
                    profileName = args[++i];
                    break;
                case "--emit" when i + 1 < args.Length:
                    emitOverride = args[++i];
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

        var profile = manifest.SelectProfile(useRelease);
        string emit = emitOverride ?? manifest.Build.Emit;
        string projectDir = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;
        string outDir = Path.GetFullPath(Path.Combine(projectDir, manifest.Build.OutDir));

        // ── Resolve source files ──────────────────────────────────────────────
        IReadOnlyList<string> sourceFiles;
        try
        {
            sourceFiles = manifest.ResolveSourceFiles(projectDir);
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.Error.WriteLine(
            $"Building {manifest.Project.Name} v{manifest.Project.Version} " +
            $"[{(useRelease ? "release" : "debug")}] ({sourceFiles.Count} file(s))");

        // ── Compile each source file ──────────────────────────────────────────
        int errors = 0;
        foreach (var sourceFile in sourceFiles)
        {
            int result = CompileSingleFile(sourceFile, emit, outDir, manifest, jsonOptions);
            if (result != 0) errors++;
        }

        if (errors > 0)
        {
            Console.Error.WriteLine($"error: build failed ({errors} file(s) with errors)");
            return 1;
        }

        Console.Error.WriteLine($"Build succeeded → {outDir}");
        return 0;
    }

    // ── Single-file compilation (shared with single-file mode) ─────────────────

    public static int CompileSingleFile(
        string sourceFile,
        string emitMode,
        string? outDir,
        ProjectManifest? manifest,
        JsonSerializerOptions jsonOptions)
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
        string baseName   = Path.GetFileNameWithoutExtension(sourceFile);

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
            case "zlib":
            {
                var zbc        = new ZbcFile(ZbcFile.CurrentVersion, sourceFile, sourceHash, ns, exports, [], irModule);
                var libExports = exports.Select(e => new ZlibExport($"{ns}.{e}", "func")).ToList();
                var zlib       = new ZlibFile(ZlibFile.CurrentVersion, ns, "0.1.0",
                                     ZmodKind.Exe, libExports, [], [zbc], $"{ns}.Main");
                string path    = Path.Combine(resolvedOutDir, baseName + ".zlib");
                WriteFile(path, JsonSerializer.Serialize(zlib, jsonOptions));
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
