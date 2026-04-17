using Z42.Core.Text;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Project;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Parser;

namespace Z42.Pipeline;

/// <summary>
/// Compiles a complete z42 package (one or more source files) to a .zpkg artifact.
/// </summary>
public static class PackageCompiler
{
    // ── Build ─────────────────────────────────────────────────────────────────

    public static int Run(
        string?               explicitToml,
        bool                  useRelease,
        string?               binFilter)
    {
        if (!TryLoadManifest(explicitToml, out var tomlPath, out var manifest)) return 1;

        string profileLabel = useRelease ? "release" : "debug";
        string projectDir   = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;
        string outDir       = Path.GetFullPath(Path.Combine(projectDir, manifest.Build.OutDir));

        if (manifest.Project.Kind == ProjectKind.Multi)
            return BuildMultiExe(manifest, projectDir, outDir, useRelease, profileLabel, binFilter);

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
            outDir);
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
        string?               binFilter)
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
                    target.Entry, sourceFiles, pack, projectDir, outDir) != 0)
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
        string                outDir)
    {
        var libsDirs    = new[] {
            Path.Combine(projectDir, "libs"),
            Path.Combine(projectDir, "artifacts", "z42", "libs"),
        };
        var nsMap       = ScanLibsForNamespaces(libsDirs);
        var depIndex    = BuildDepIndex(libsDirs);
        var allTsig     = LoadAllTsig(libsDirs);
        ScanZbcForNamespaces(BuildZbcScanDirs(), nsMap);

        var units = TryCompileSourceFiles(sourceFiles, depIndex, allTsig);
        if (units is null) return 1;

        WarnUnresolvedUsings(units, nsMap);

        var dependencies = BuildDependencyMap(units, nsMap);
        string cacheDir  = Path.Combine(projectDir, ".cache");
        var zbcFiles     = units.Select(u => u.ToZbcFile()).ToList();
        var exportedModules = units
            .Where(u => u.ExportedTypes != null)
            .Select(u => u.ExportedTypes!)
            .ToList();

        ZpkgFile zpkg;
        if (pack)
        {
            zpkg = ZpkgBuilder.BuildPacked(name, version, kind, entry, zbcFiles, dependencies, exportedModules);
        }
        else
        {
            var (indexedZpkg, writtenPaths) = ZpkgBuilder.BuildIndexed(
                name, version, kind, entry, zbcFiles, dependencies,
                projectDir, cacheDir, outDir);
            foreach (var p in writtenPaths)
                Console.Error.WriteLine($"wrote → {p}");
            zpkg = indexedZpkg;
        }

        string zpkgPath = ZpkgBuilder.WriteZpkg(zpkg, name, outDir);
        Console.Error.WriteLine($"wrote → {zpkgPath}");
        Console.Error.WriteLine($"    Finished → {outDir}");
        return 0;
    }

    // ── Build target sub-steps ────────────────────────────────────────────────

    /// Scan .zpkg files in libs dirs and build a namespace → filename map.
    static Dictionary<string, string> ScanLibsForNamespaces(string[] libsDirs)
    {
        var nsMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var libsDir in libsDirs)
        {
            if (!Directory.Exists(libsDir)) continue;
            foreach (var zpkgFile in Directory.EnumerateFiles(libsDir, "*.zpkg"))
            {
                try
                {
                    var bytes = File.ReadAllBytes(zpkgFile);
                    var ns    = ZpkgReader.ReadNamespaces(bytes);
                    string fname = Path.GetFileName(zpkgFile);
                    foreach (var n in ns) nsMap.TryAdd(n, fname);
                }
                catch { /* skip malformed zpkg */ }
            }
        }
        return nsMap;
    }

    /// Return the list of directories to scan for .zbc files (Z42_PATH + cwd + cwd/modules).
    static List<string> BuildZbcScanDirs()
    {
        var dirs   = new List<string>();
        var z42Path = Environment.GetEnvironmentVariable("Z42_PATH");
        if (!string.IsNullOrEmpty(z42Path))
            dirs.AddRange(z42Path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        var cwd = Directory.GetCurrentDirectory();
        dirs.Add(cwd);
        dirs.Add(Path.Combine(cwd, "modules"));
        return dirs;
    }

    /// Scan .zbc files and add/override namespace → filename entries in <paramref name="nsMap"/>.
    static void ScanZbcForNamespaces(IEnumerable<string> dirs, Dictionary<string, string> nsMap)
    {
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var zbcFile in Directory.EnumerateFiles(dir, "*.zbc"))
            {
                try
                {
                    var bytes = File.ReadAllBytes(zbcFile);
                    var ns    = ZbcReader.ReadNamespace(bytes);
                    if (string.IsNullOrEmpty(ns)) continue;
                    nsMap[ns] = Path.GetFileName(zbcFile);
                }
                catch { /* skip malformed zbc */ }
            }
        }
    }

    /// Compile all source files; returns null and prints an error count if any file fails.
    static List<CompiledUnit>? TryCompileSourceFiles(
        IReadOnlyList<string>     sourceFiles,
        DependencyIndex           depIndex,
        List<ExportedModule>?     allTsig = null)
    {
        var units  = new List<CompiledUnit>();
        int errors = 0;
        foreach (var sourceFile in sourceFiles)
        {
            var unit = CompileFile(sourceFile, depIndex, allTsig);
            if (unit is null) { errors++; continue; }
            units.Add(unit);
        }
        if (errors > 0)
        { Console.Error.WriteLine($"error: build failed ({errors} file(s) with errors)"); return null; }
        return units;
    }

    /// Emit warnings for `using` declarations that cannot be resolved in <paramref name="nsMap"/>.
    static void WarnUnresolvedUsings(
        IReadOnlyList<CompiledUnit>  units,
        Dictionary<string, string>   nsMap)
    {
        if (nsMap.Count == 0) return;
        foreach (var unit in units)
            foreach (var usingNs in unit.Usings)
                if (!nsMap.ContainsKey(usingNs))
                    Console.Error.WriteLine(
                        $"warning: using '{usingNs}' in {Path.GetFileName(unit.SourceFile)}: namespace not found in any library");
    }

    /// Build the dependency list (file → namespaces) from resolved usings and dependency calls.
    static List<ZpkgDep> BuildDependencyMap(
        IReadOnlyList<CompiledUnit>  units,
        Dictionary<string, string>   nsMap)
    {
        var depMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void AddDepNs(string ns)
        {
            if (!nsMap.TryGetValue(ns, out var depFile)) return;
            if (!depMap.TryGetValue(depFile, out var nsList))
            {
                nsList = [];
                depMap[depFile] = nsList;
            }
            if (!nsList.Contains(ns)) nsList.Add(ns);
        }

        foreach (var unit in units)
        {
            foreach (var usingNs in unit.Usings)     AddDepNs(usingNs);
            foreach (var stdNs in unit.UsedDepNamespaces) AddDepNs(stdNs);
        }
        return [.. depMap.Select(kv => new ZpkgDep(kv.Key, kv.Value))];
    }

    // ── Per-file helpers ──────────────────────────────────────────────────────

    static CompiledUnit? CompileFile(
        string sourceFile, DependencyIndex depIndex,
        List<ExportedModule>? allTsig = null)
    {
        string source;
        try   { source = File.ReadAllText(sourceFile); }
        catch { Console.Error.WriteLine($"error: cannot read {sourceFile}"); return null; }

        // Pre-parse to extract using declarations for TSIG filtering.
        // PipelineCore.Compile does the full lex+parse internally, so we do a
        // lightweight pre-scan for `using` lines. This avoids double-parsing by
        // extracting usings from the source text directly.
        var usings = ExtractUsings(source);
        ImportedSymbols? imported = null;
        if (allTsig is { Count: > 0 })
            imported = ImportedSymbolLoader.Load(allTsig, usings);

        var result = PipelineCore.Compile(source, sourceFile, depIndex, imported: imported);
        result.Diags.PrintAll();
        if (result.Diags.HasErrors || result.Module is null) return null;

        string ns         = result.Namespace ?? "main";
        string sourceHash = Sha256Hex(source);
        var    exports    = result.Module.Functions.Select(f => f.Name).ToList();
        return new CompiledUnit(sourceFile, ns, sourceHash, exports, result.Module,
            result.Usings.ToList(), result.UsedDepNamespaces.ToList(), result.ExportedTypes);
    }

    static bool CheckFile(string sourceFile)
    {
        string source;
        try   { source = File.ReadAllText(sourceFile); }
        catch { Console.Error.WriteLine($"error: cannot read {sourceFile}"); return false; }

        var result = PipelineCore.Compile(source, sourceFile, DependencyIndex.Empty);
        result.Diags.PrintAll();
        return !result.Diags.HasErrors;
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

    static string Sha256Hex(string text) => CompilerUtils.Sha256Hex(text);

    /// Load all TSIG sections from lib zpkgs for reference compilation.
    static List<ExportedModule> LoadAllTsig(string[] libsDirs)
    {
        var result = new List<ExportedModule>();
        foreach (var dir in libsDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var zpkgPath in Directory.EnumerateFiles(dir, "*.zpkg"))
            {
                try
                {
                    var bytes = File.ReadAllBytes(zpkgPath);
                    var meta  = ZpkgReader.ReadMeta(bytes);
                    if (meta.Kind != ZpkgKind.Lib) continue;
                    result.AddRange(ZpkgReader.ReadTsig(bytes));
                }
                catch { /* skip malformed */ }
            }
        }
        return result;
    }

    /// Lightweight extraction of `using Ns.Name;` declarations from source text.
    /// Avoids full lex/parse — just scans line-by-line for `using` statements.
    static List<string> ExtractUsings(string source)
    {
        var result = new List<string>();
        foreach (var rawLine in source.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("using ") && line.EndsWith(";"))
            {
                var ns = line[6..^1].Trim();
                if (ns.Length > 0 && !ns.Contains(' '))
                    result.Add(ns);
            }
        }
        return result;
    }

    /// Load lib-kind zpkgs from the given directories and build a DependencyIndex
    /// from their packed modules. Silently skips malformed or non-lib packages.
    public static DependencyIndex BuildDepIndex(string[] libsDirs)
    {
        var modules = new List<(IrModule Module, string Namespace)>();
        foreach (var dir in libsDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var zpkgPath in Directory.EnumerateFiles(dir, "*.zpkg"))
            {
                try
                {
                    var bytes = File.ReadAllBytes(zpkgPath);
                    var meta  = ZpkgReader.ReadMeta(bytes);
                    if (meta.Kind != ZpkgKind.Lib) continue;
                    foreach (var (mod, ns) in ZpkgReader.ReadModules(bytes))
                        modules.Add((mod, ns));
                }
                catch { /* skip malformed */ }
            }
        }
        return DependencyIndex.Build(modules);
    }
}

// ── Compiled unit ─────────────────────────────────────────────────────────────

public sealed record CompiledUnit(
    string          SourceFile,
    string          Namespace,
    string          SourceHash,
    List<string>    Exports,
    IrModule        Module,
    List<string>    Usings,
    List<string>    UsedDepNamespaces,
    ExportedModule? ExportedTypes = null
)
{
    public ZbcFile ToZbcFile() =>
        new ZbcFile(ZbcFile.CurrentVersion, SourceFile, SourceHash, Namespace, Exports, [], Module);
}
