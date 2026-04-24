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
            outDir,
            manifest.Dependencies);
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
                    target.Entry, sourceFiles, pack, projectDir, outDir,
                    manifest.Dependencies) != 0)
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
        DependencySection?    declaredDeps = null)
    {
        // L3-G4g: libsDirs includes projectDir/libs, projectDir/artifacts/z42/libs,
        // plus a walk-up search for the repo-level `artifacts/z42/libs` so stdlib
        // packages compiling against each other (e.g. z42.collections → z42.core)
        // can resolve their dependencies.
        var libsDirs    = BuildLibsDirs(projectDir);
        var tsigCache   = new TsigCache();
        var nsMap       = ScanLibsForNamespaces(libsDirs, tsigCache, declaredDeps);
        var depIndex    = BuildDepIndex(libsDirs, declaredDeps);
        ScanZbcForNamespaces(BuildZbcScanDirs(), nsMap);

        var units = TryCompileSourceFiles(sourceFiles, depIndex, tsigCache);
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
    /// Build the list of directories to scan for dependency `.zpkg` files.
    /// Includes project-local `libs/` and `artifacts/z42/libs/`, plus a walk-up search
    /// for the repo-level `artifacts/z42/libs/` (so stdlib packages built one after
    /// another can see each other).
    static string[] BuildLibsDirs(string projectDir)
    {
        var dirs = new List<string>
        {
            Path.Combine(projectDir, "libs"),
            Path.Combine(projectDir, "artifacts", "z42", "libs"),
        };
        var dir = new DirectoryInfo(projectDir).Parent;
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "z42", "libs");
            if (Directory.Exists(candidate) && !dirs.Contains(candidate))
                dirs.Add(candidate);
            dir = dir.Parent;
        }
        return dirs.ToArray();
    }

    /// Also populates the TsigCache with namespace → zpkg path mappings for on-demand loading.
    /// When `declaredDeps` has entries, only declared packages + stdlib (z42.*) are visible.
    static Dictionary<string, string> ScanLibsForNamespaces(
        string[] libsDirs, TsigCache? tsigCache = null,
        DependencySection? declaredDeps = null)
    {
        var allowedPkgs = declaredDeps is { IsDeclared: true }
            ? declaredDeps.Entries.Select(d => d.Name).ToHashSet(StringComparer.Ordinal)
            : null; // null = auto-scan (allow all)

        var nsMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var libsDir in libsDirs)
        {
            if (!Directory.Exists(libsDir)) continue;
            foreach (var zpkgFile in Directory.EnumerateFiles(libsDir, "*.zpkg"))
            {
                try
                {
                    var bytes = File.ReadAllBytes(zpkgFile);
                    var meta  = ZpkgReader.ReadMeta(bytes);
                    // Filter: stdlib (z42.*) always visible; third-party only if declared.
                    bool isStdlib = meta.Name.StartsWith("z42.", StringComparison.Ordinal);
                    if (allowedPkgs != null && !isStdlib && !allowedPkgs.Contains(meta.Name))
                        continue;

                    string fname = Path.GetFileName(zpkgFile);
                    foreach (var n in meta.Namespaces)
                    {
                        nsMap.TryAdd(n, fname);
                        tsigCache?.RegisterNamespace(n, zpkgFile);
                    }
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
        TsigCache?                tsigCache = null)
    {
        var units  = new List<CompiledUnit>();
        int errors = 0;
        foreach (var sourceFile in sourceFiles)
        {
            var unit = CompileFile(sourceFile, depIndex, tsigCache);
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
        TsigCache? tsigCache = null)
    {
        string source;
        try   { source = File.ReadAllText(sourceFile); }
        catch { Console.Error.WriteLine($"error: cannot read {sourceFile}"); return null; }

        // Stdlib is always visible at compile time (no `using` needed).
        // DEPS section records only actually-called namespaces via UsedDepNamespaces.
        ImportedSymbols? imported = null;
        if (tsigCache != null)
        {
            var tsigModules = tsigCache.LoadAll();
            if (tsigModules.Count > 0)
            {
                var allNs = tsigModules.Select(m => m.Namespace).Distinct().ToList();
                imported = ImportedSymbolLoader.Load(tsigModules, allNs);
            }
        }

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

    /// Lightweight extraction of `using Ns.Name;` declarations from source text.
    /// Avoids full lex/parse — just scans line-by-line for `using` statements.
    public static List<string> ExtractUsingsPublic(string source) => ExtractUsings(source);

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
    /// When `declaredDeps` has entries, only declared packages + stdlib are loaded.
    public static DependencyIndex BuildDepIndex(
        string[] libsDirs, DependencySection? declaredDeps = null)
    {
        var allowedPkgs = declaredDeps is { IsDeclared: true }
            ? declaredDeps.Entries.Select(d => d.Name).ToHashSet(StringComparer.Ordinal)
            : null;

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
                    bool isStdlib = meta.Name.StartsWith("z42.", StringComparison.Ordinal);
                    if (allowedPkgs != null && !isStdlib && !allowedPkgs.Contains(meta.Name))
                        continue;
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

// ── On-demand TSIG loader with caching ───────────────────────────────────────

/// <summary>
/// Caches zpkg TSIG sections. Loaded on demand when a source file's <c>using</c>
/// references a namespace provided by a zpkg. Each zpkg is read at most once.
/// </summary>
public sealed class TsigCache
{
    // namespace → zpkg full paths (C# assembly model: a namespace may be
    // declared by multiple zpkgs, e.g. `Std.Collections` is split across
    // `z42.core.zpkg` (List/Dictionary) and `z42.collections.zpkg` (Queue/Stack)
    // after the 2026-04-25 stdlib reorganisation).
    private readonly Dictionary<string, List<string>> _nsToPaths = new(StringComparer.Ordinal);
    // zpkg path → cached TSIG modules (loaded on first access)
    private readonly Dictionary<string, List<ExportedModule>> _cache = new(StringComparer.Ordinal);

    /// Register a namespace → zpkg path mapping (called during lib scanning).
    /// Multiple zpkgs may register the same namespace — all paths are preserved
    /// so callers see the union of their exported types.
    public void RegisterNamespace(string ns, string zpkgPath)
    {
        if (!_nsToPaths.TryGetValue(ns, out var list))
        {
            list = new List<string>();
            _nsToPaths[ns] = list;
        }
        if (!list.Contains(zpkgPath)) list.Add(zpkgPath);
    }

    /// Load all registered TSIG modules (used by tests that need all namespaces).
    public List<ExportedModule> LoadAll()
    {
        var allPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paths in _nsToPaths.Values)
            foreach (var p in paths) allPaths.Add(p);
        var result = new List<ExportedModule>();
        foreach (var path in allPaths)
            result.AddRange(LoadZpkg(path));
        return result;
    }

    /// Load TSIG modules for the given using declarations. Only reads zpkg files
    /// that provide at least one of the requested namespaces; caches results.
    public List<ExportedModule> LoadForUsings(IReadOnlyList<string> usings)
    {
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ns in usings)
            if (_nsToPaths.TryGetValue(ns, out var paths))
                foreach (var p in paths) needed.Add(p);

        var result = new List<ExportedModule>();
        foreach (var path in needed)
            result.AddRange(LoadZpkg(path));
        return result;
    }

    private List<ExportedModule> LoadZpkg(string zpkgPath)
    {
        if (_cache.TryGetValue(zpkgPath, out var cached)) return cached;
        try
        {
            var bytes   = File.ReadAllBytes(zpkgPath);
            var meta    = ZpkgReader.ReadMeta(bytes);
            if (meta.Kind != ZpkgKind.Lib) { _cache[zpkgPath] = []; return []; }
            var modules = ZpkgReader.ReadTsig(bytes);
            _cache[zpkgPath] = modules;
            return modules;
        }
        catch
        {
            _cache[zpkgPath] = [];
            return [];
        }
    }
}
