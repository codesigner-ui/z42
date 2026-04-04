using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Z42.Build;
using Z42.Compiler.Codegen;
using Z42.Compiler.Diagnostics;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;
using Z42.Compiler.TypeCheck;
using Z42.IR;

namespace Z42.Driver;

static class BuildCommand
{
    public static int Run(string[] args, JsonSerializerOptions jsonOptions)
    {
        // ── Parse build flags ─────────────────────────────────────────────────
        bool    useRelease   = false;
        string? explicitToml = null;
        string? exeFilter    = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--release":
                    useRelease = true;
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
        string projectDir   = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;
        string outDir       = Path.GetFullPath(Path.Combine(projectDir, manifest.Build.OutDir));

        // ── Route: multi-exe vs single-target ────────────────────────────────
        if (manifest.Project.Kind == Z42.Build.ProjectKind.Multi)
            return BuildMultiExe(manifest, projectDir, outDir, useRelease, profileLabel, exeFilter, jsonOptions);

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

        bool pack = manifest.ResolvePack(useRelease);
        return BuildTarget(
            manifest.Project.Name,
            manifest.Project.Version,
            manifest.Project.Kind == Z42.Build.ProjectKind.Lib ? ZpkgKind.Lib : ZpkgKind.Exe,
            manifest.Project.Entry,
            sourceFiles,
            pack,
            projectDir,
            outDir,
            jsonOptions);
    }

    static int BuildMultiExe(
        ProjectManifest manifest,
        string projectDir,
        string outDir,
        bool useRelease,
        string profileLabel,
        string? exeFilter,
        JsonSerializerOptions jsonOptions)
    {
        var targets = manifest.ExeTargets;

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

            bool pack = manifest.ResolvePack(useRelease, target.Pack);

            if (BuildTarget(
                    target.Name,
                    manifest.Project.Version,
                    ZpkgKind.Exe,
                    target.Entry,
                    sourceFiles,
                    pack,
                    projectDir,
                    outDir,
                    jsonOptions) != 0)
                errors++;
        }

        if (errors > 0) { Console.Error.WriteLine($"error: build failed ({errors} error(s))"); return 1; }
        Console.Error.WriteLine($"Build succeeded → {outDir}");
        return 0;
    }

    // ── Build a single named target → dist/<name>.zpkg ────────────────────────

    static int BuildTarget(
        string                name,
        string                version,
        ZpkgKind              kind,
        string?               entry,
        IReadOnlyList<string> sourceFiles,
        bool                  pack,
        string                projectDir,
        string                outDir,
        JsonSerializerOptions jsonOptions)
    {
        var units = new List<CompiledUnit>();
        int errors = 0;

        foreach (var sourceFile in sourceFiles)
        {
            var unit = CompileFile(sourceFile);
            if (unit is null) { errors++; continue; }
            units.Add(unit);
        }

        if (errors > 0)
        {
            Console.Error.WriteLine($"error: build failed ({errors} file(s) with errors)");
            return 1;
        }

        // Assemble .zpkg
        string cacheDir = Path.Combine(projectDir, ".cache");
        var exports = units
            .SelectMany(u => u.Exports.Select(e => new ZpkgExport($"{u.Namespace}.{e}", "func")))
            .ToList();

        ZpkgFile zpkg;
        if (pack)
        {
            zpkg = new ZpkgFile(
                Name:         name,
                Version:      version,
                Kind:         kind,
                Mode:         ZpkgMode.Packed,
                Exports:      exports,
                Dependencies: [],
                Files:        [],
                Modules:      units.Select(u => u.ToZbcFile()).ToList(),
                Entry:        entry
            );
        }
        else
        {
            // Write per-file .zbc into .cache/
            var fileEntries = new List<ZpkgFileEntry>();
            foreach (var unit in units)
            {
                string relSrc  = Path.GetRelativePath(projectDir, unit.SourceFile);
                string zbcPath = Path.Combine(cacheDir, Path.ChangeExtension(relSrc, ".zbc"));
                Directory.CreateDirectory(Path.GetDirectoryName(zbcPath)!);
                File.WriteAllBytes(zbcPath, Z42.IR.BinaryFormat.ZbcWriter.Write(unit.Module, unit.Exports));
                Console.Error.WriteLine($"wrote → {zbcPath}");

                string zbcRel = Path.GetRelativePath(outDir, zbcPath);
                fileEntries.Add(new ZpkgFileEntry(unit.SourceFile, zbcRel, unit.SourceHash, unit.Exports));
            }

            zpkg = new ZpkgFile(
                Name:         name,
                Version:      version,
                Kind:         kind,
                Mode:         ZpkgMode.Indexed,
                Exports:      exports,
                Dependencies: [],
                Files:        fileEntries,
                Modules:      [],
                Entry:        entry
            );
        }

        Directory.CreateDirectory(outDir);
        string zpkgPath = Path.Combine(outDir, name + ".zpkg");
        File.WriteAllText(zpkgPath, JsonSerializer.Serialize(zpkg, jsonOptions));
        Console.Error.WriteLine($"wrote → {zpkgPath}");
        Console.Error.WriteLine($"Build succeeded → {outDir}");
        return 0;
    }

    // ── Per-file compilation ──────────────────────────────────────────────────

    static CompiledUnit? CompileFile(string sourceFile)
    {
        string source;
        try   { source = File.ReadAllText(sourceFile); }
        catch { Console.Error.WriteLine($"error: cannot read {sourceFile}"); return null; }

        var tokens = new Lexer(source, sourceFile).Tokenize();

        CompilationUnit cu;
        try   { cu = new Parser(tokens).ParseCompilationUnit(); }
        catch (ParseException ex)
        {
            Console.Error.WriteLine(
                $"parse error at {ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
            return null;
        }

        var diags = new DiagnosticBag();
        new TypeChecker(diags).Check(cu);
        if (diags.PrintAll()) return null;

        IrModule irModule;
        try   { irModule = new IrGen().Generate(cu); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"codegen error: {ex.Message}");
            return null;
        }

        string ns         = cu.Namespace ?? "main";
        string sourceHash = Sha256Hex(source);
        var    exports    = irModule.Functions.Select(f => f.Name).ToList();
        return new CompiledUnit(sourceFile, ns, sourceHash, exports, irModule);
    }

    static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

// ── Compiled unit (per-file result) ──────────────────────────────────────────

sealed record CompiledUnit(
    string       SourceFile,
    string       Namespace,
    string       SourceHash,
    List<string> Exports,
    IrModule     Module
)
{
    public ZbcFile ToZbcFile() =>
        new ZbcFile(ZbcFile.CurrentVersion, SourceFile, SourceHash, Namespace, Exports, [], Module);
}
