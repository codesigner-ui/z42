using System.CommandLine;
using System.CommandLine.Invocation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Z42.Compiler.Codegen;
using Z42.Compiler.Diagnostics;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;
using Z42.Compiler.TypeCheck;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Project;

namespace Z42.Driver;

static class BuildCommand
{
    // ── Command factories ─────────────────────────────────────────────────────

    public static Command Create(JsonSerializerOptions jsonOptions)
    {
        var cmd         = new Command("build", "Build a z42 project from a manifest");
        var manifestArg = ManifestArg();
        var releaseOpt  = new Option<bool>("--release", "Build with the release profile");
        var binOpt      = new Option<string?>("--bin", "Build only the named [[exe]] target");

        cmd.AddArgument(manifestArg);
        cmd.AddOption(releaseOpt);
        cmd.AddOption(binOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var manifest = ctx.ParseResult.GetValueForArgument(manifestArg);
            var release  = ctx.ParseResult.GetValueForOption(releaseOpt);
            var bin      = ctx.ParseResult.GetValueForOption(binOpt);
            ctx.ExitCode = Run(manifest, release, bin, jsonOptions);
        });

        return cmd;
    }

    public static Command CreateCheck()
    {
        var cmd         = new Command("check", "Type-check a project without emitting artifacts");
        var manifestArg = ManifestArg();
        var binOpt      = new Option<string?>("--bin", "Check only the named [[exe]] target");

        cmd.AddArgument(manifestArg);
        cmd.AddOption(binOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var manifest = ctx.ParseResult.GetValueForArgument(manifestArg);
            var bin      = ctx.ParseResult.GetValueForOption(binOpt);
            ctx.ExitCode = RunCheck(manifest, bin);
        });

        return cmd;
    }

    static Argument<string?> ManifestArg() =>
        new("manifest", () => null, "Path to .z42.toml (auto-discovered if omitted)")
        {
            Arity = ArgumentArity.ZeroOrOne
        };

    // ── Build ─────────────────────────────────────────────────────────────────

    public static int Run(
        string?               explicitToml,
        bool                  useRelease,
        string?               binFilter,
        JsonSerializerOptions jsonOptions)
    {
        if (!TryLoadManifest(explicitToml, out var tomlPath, out var manifest)) return 1;

        string profileLabel = useRelease ? "release" : "debug";
        string projectDir   = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;
        string outDir       = Path.GetFullPath(Path.Combine(projectDir, manifest.Build.OutDir));

        if (manifest.Project.Kind == ProjectKind.Multi)
            return BuildMultiExe(manifest, projectDir, outDir, useRelease, profileLabel, binFilter, jsonOptions);

        if (binFilter is not null)
        {
            Console.Error.WriteLine("error: --bin is only valid for projects with [[exe]] targets");
            return 1;
        }

        if (!TryResolveFiles(manifest, projectDir, null, out var sourceFiles)) return 1;

        Console.Error.WriteLine(
            $"   Compiling {manifest.Project.Name} v{manifest.Project.Version} [{profileLabel}]");

        bool pack = manifest.ResolvePack(useRelease);
        return BuildTarget(
            manifest.Project.Name,
            manifest.Project.Version,
            manifest.Project.Kind == ProjectKind.Lib ? ZpkgKind.Lib : ZpkgKind.Exe,
            manifest.Project.Entry,
            sourceFiles,
            pack,
            projectDir,
            outDir,
            jsonOptions);
    }

    // ── Check ─────────────────────────────────────────────────────────────────

    public static int RunCheck(string? explicitToml, string? binFilter)
    {
        if (!TryLoadManifest(explicitToml, out var tomlPath, out var manifest)) return 1;

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;

        Console.Error.WriteLine(
            $"    Checking {manifest.Project.Name} v{manifest.Project.Version}");

        var fileSets = new List<IReadOnlyList<string>>();

        if (manifest.Project.Kind == ProjectKind.Multi)
        {
            var targets = manifest.ExeTargets;
            if (binFilter is not null)
            {
                targets = targets.Where(t => t.Name == binFilter).ToList();
                if (targets.Count == 0)
                { Console.Error.WriteLine($"error: no [[exe]] named '{binFilter}'"); return 1; }
            }
            foreach (var t in targets)
            {
                if (!TryResolveFiles(manifest, projectDir, t, out var f)) return 1;
                fileSets.Add(f);
            }
        }
        else
        {
            if (binFilter is not null)
            { Console.Error.WriteLine("error: --bin is only valid for projects with [[exe]] targets"); return 1; }
            if (!TryResolveFiles(manifest, projectDir, null, out var f)) return 1;
            fileSets.Add(f);
        }

        int errors = fileSets
            .SelectMany(s => s)
            .Count(file => !CheckFile(file));

        if (errors > 0) { Console.Error.WriteLine($"error: check failed ({errors} file(s) with errors)"); return 1; }
        Console.Error.WriteLine($"    Finished checking → ok");
        return 0;
    }

    // ── Multi-exe build ───────────────────────────────────────────────────────

    static int BuildMultiExe(
        ProjectManifest       manifest,
        string                projectDir,
        string                outDir,
        bool                  useRelease,
        string                profileLabel,
        string?               binFilter,
        JsonSerializerOptions jsonOptions)
    {
        var targets = manifest.ExeTargets;
        if (binFilter is not null)
        {
            targets = targets.Where(t => t.Name == binFilter).ToList();
            if (targets.Count == 0)
            { Console.Error.WriteLine($"error: no [[exe]] named '{binFilter}'"); return 1; }
        }

        Console.Error.WriteLine(
            $"   Compiling {manifest.Project.Name} v{manifest.Project.Version} " +
            $"[{profileLabel}] ({targets.Count} target(s))");

        int errors = 0;
        foreach (var target in targets)
        {
            Console.Error.WriteLine($"   Compiling {target.Name} ({target.Entry})");
            if (!TryResolveFiles(manifest, projectDir, target, out var sourceFiles))
            { errors++; continue; }
            bool pack = manifest.ResolvePack(useRelease, target.Pack);
            if (BuildTarget(target.Name, manifest.Project.Version, ZpkgKind.Exe,
                    target.Entry, sourceFiles, pack, projectDir, outDir, jsonOptions) != 0)
                errors++;
        }

        if (errors > 0) { Console.Error.WriteLine($"error: build failed ({errors} error(s))"); return 1; }
        Console.Error.WriteLine($"    Finished [{profileLabel}] → {outDir}");
        return 0;
    }

    // ── Single target build ───────────────────────────────────────────────────

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
        // ── 4.1: Scan .zpkg files in libs/ dirs for namespace → filename mapping ──
        var nsMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var libsDirs = new[]
        {
            Path.Combine(projectDir, "libs"),
            Path.Combine(projectDir, "artifacts", "z42", "libs"),
        };
        foreach (var libsDir in libsDirs)
        {
            if (!Directory.Exists(libsDir)) continue;
            foreach (var zpkgFile in Directory.EnumerateFiles(libsDir, "*.zpkg"))
            {
                try
                {
                    var text = File.ReadAllText(zpkgFile);
                    var pkg  = JsonSerializer.Deserialize<ZpkgFile>(text, jsonOptions);
                    if (pkg is null) continue;
                    string fname = Path.GetFileName(zpkgFile);
                    foreach (var ns in pkg.Namespaces)
                        nsMap.TryAdd(ns, fname);
                }
                catch { /* skip malformed zpkg */ }
            }
        }

        // ── 4.2: Scan .zbc files in Z42_PATH and cwd/modules/ (zbc overrides zpkg) ──
        var zbcScanDirs = new List<string>();
        var z42Path = Environment.GetEnvironmentVariable("Z42_PATH");
        if (!string.IsNullOrEmpty(z42Path))
            zbcScanDirs.AddRange(z42Path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        var cwd = Directory.GetCurrentDirectory();
        zbcScanDirs.Add(cwd);
        zbcScanDirs.Add(Path.Combine(cwd, "modules"));

        foreach (var dir in zbcScanDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var zbcFile in Directory.EnumerateFiles(dir, "*.zbc"))
            {
                try
                {
                    var bytes = File.ReadAllBytes(zbcFile);
                    var ns    = ZbcReader.ReadNamespace(bytes);
                    if (string.IsNullOrEmpty(ns)) continue;
                    string fname = Path.GetFileName(zbcFile);
                    nsMap[ns] = fname; // zbc overrides zpkg for same namespace
                }
                catch { /* skip malformed zbc */ }
            }
        }

        // ── Compile all source files ──────────────────────────────────────────────
        var units  = new List<CompiledUnit>();
        int errors = 0;

        foreach (var sourceFile in sourceFiles)
        {
            var unit = CompileFile(sourceFile);
            if (unit is null) { errors++; continue; }
            units.Add(unit);
        }

        if (errors > 0)
        { Console.Error.WriteLine($"error: build failed ({errors} file(s) with errors)"); return 1; }

        // ── 4.3: Warn on unresolved namespaces in `using` declarations ───────────
        if (nsMap.Count > 0)
        {
            foreach (var unit in units)
            {
                foreach (var usingNs in unit.Usings)
                {
                    if (!nsMap.ContainsKey(usingNs))
                        Console.Error.WriteLine(
                            $"warning: using '{usingNs}' in {Path.GetFileName(unit.SourceFile)}: namespace not found in any library");
                }
            }
        }

        // ── 4.4: Build dependencies list from resolved usings ────────────────────
        var depMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            foreach (var usingNs in unit.Usings)
            {
                if (nsMap.TryGetValue(usingNs, out var depFile))
                {
                    if (!depMap.TryGetValue(depFile, out var nsList))
                    {
                        nsList = [];
                        depMap[depFile] = nsList;
                    }
                    if (!nsList.Contains(usingNs))
                        nsList.Add(usingNs);
                }
            }
        }
        var dependencies = depMap
            .Select(kv => new ZpkgDep(kv.Key, kv.Value))
            .ToList();

        string cacheDir = Path.Combine(projectDir, ".cache");
        var namespaces = units.Select(u => u.Namespace).Distinct().ToList();
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
                Namespaces:   namespaces,
                Exports:      exports,
                Dependencies: dependencies,
                Files:        [],
                Modules:      units.Select(u => u.ToZbcFile()).ToList(),
                Entry:        entry
            );
        }
        else
        {
            var fileEntries = new List<ZpkgFileEntry>();
            foreach (var unit in units)
            {
                string relSrc  = Path.GetRelativePath(projectDir, unit.SourceFile);
                string zbcPath = Path.Combine(cacheDir, Path.ChangeExtension(relSrc, ".zbc"));
                Directory.CreateDirectory(Path.GetDirectoryName(zbcPath)!);
                File.WriteAllBytes(zbcPath, ZbcWriter.Write(unit.Module, ZbcFlags.Stripped));
                Console.Error.WriteLine($"wrote → {zbcPath}");

                string zbcRel = Path.GetRelativePath(outDir, zbcPath);
                fileEntries.Add(new ZpkgFileEntry(unit.SourceFile, zbcRel, unit.SourceHash, unit.Exports));
            }

            zpkg = new ZpkgFile(
                Name:         name,
                Version:      version,
                Kind:         kind,
                Mode:         ZpkgMode.Indexed,
                Namespaces:   namespaces,
                Exports:      exports,
                Dependencies: dependencies,
                Files:        fileEntries,
                Modules:      [],
                Entry:        entry
            );
        }

        Directory.CreateDirectory(outDir);
        string zpkgPath = Path.Combine(outDir, name + ".zpkg");
        File.WriteAllText(zpkgPath, JsonSerializer.Serialize(zpkg, jsonOptions));
        Console.Error.WriteLine($"wrote → {zpkgPath}");
        Console.Error.WriteLine($"    Finished → {outDir}");
        return 0;
    }

    // ── Per-file helpers ──────────────────────────────────────────────────────

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
                $"error[E0001]: {Path.GetFileName(sourceFile)}:{ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
            return null;
        }

        var diags = new DiagnosticBag();
        new TypeChecker(diags).Check(cu);
        if (diags.PrintAll()) return null;

        IrModule irModule;
        try   { irModule = new IrGen().Generate(cu); }
        catch (Exception ex) { Console.Error.WriteLine($"error: codegen: {ex.Message}"); return null; }

        string ns         = cu.Namespace ?? "main";
        string sourceHash = Sha256Hex(source);
        var    exports    = irModule.Functions.Select(f => f.Name).ToList();
        var    usings     = cu.Usings.ToList();
        return new CompiledUnit(sourceFile, ns, sourceHash, exports, irModule, usings);
    }

    static bool CheckFile(string sourceFile)
    {
        string source;
        try   { source = File.ReadAllText(sourceFile); }
        catch { Console.Error.WriteLine($"error: cannot read {sourceFile}"); return false; }

        var tokens = new Lexer(source, sourceFile).Tokenize();

        CompilationUnit cu;
        try   { cu = new Parser(tokens).ParseCompilationUnit(); }
        catch (ParseException ex)
        {
            Console.Error.WriteLine(
                $"error[E0001]: {Path.GetFileName(sourceFile)}:{ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
            return false;
        }

        var diags = new DiagnosticBag();
        new TypeChecker(diags).Check(cu);
        return !diags.PrintAll();
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    static bool TryLoadManifest(
        string? explicitToml,
        out string tomlPath,
        out ProjectManifest manifest)
    {
        try
        {
            tomlPath = ProjectManifest.Discover(Directory.GetCurrentDirectory(), explicitToml);
            manifest = ProjectManifest.Load(tomlPath);
            return true;
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            tomlPath = "";
            manifest = null!;
            return false;
        }
    }

    static bool TryResolveFiles(
        ProjectManifest manifest,
        string projectDir,
        ExeTarget? target,
        out IReadOnlyList<string> files)
    {
        try
        {
            files = target is null
                ? manifest.ResolveSourceFiles(projectDir)
                : manifest.ResolveSourceFiles(projectDir, target);
            return true;
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            files = [];
            return false;
        }
    }

    static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

// ── Compiled unit ─────────────────────────────────────────────────────────────

sealed record CompiledUnit(
    string       SourceFile,
    string       Namespace,
    string       SourceHash,
    List<string> Exports,
    IrModule     Module,
    List<string> Usings
)
{
    public ZbcFile ToZbcFile() =>
        new ZbcFile(ZbcFile.CurrentVersion, SourceFile, SourceHash, Namespace, Exports, [], Module);
}
