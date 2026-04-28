using Z42.Core;
using Z42.IR;
using Z42.Project;

namespace Z42.Pipeline;

/// <summary>
/// Caches zpkg TSIG sections. Loaded on demand when a source file's <c>using</c>
/// references a namespace provided by a zpkg. Each zpkg is read at most once.
///
/// strict-using-resolution (2026-04-28): tracks package name per zpkg path
/// so callers can filter by activated packages (prelude + user usings).
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
    // zpkg path → package name (from META section). Lazy filled on first LoadZpkg.
    private readonly Dictionary<string, string> _pathToPkg = new(StringComparer.Ordinal);

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

    /// strict-using-resolution: load TSIG modules from all zpkgs whose package
    /// name is in <paramref name="activatedPackages"/>. Returns module list
    /// plus a per-module map (module → packageName) for downstream filtering.
    public (List<ExportedModule> Modules, Dictionary<ExportedModule, string> PackageOf)
        LoadForPackages(IReadOnlyCollection<string> activatedPackages)
    {
        // Need to know packageName per path → may need to load every registered
        // zpkg's META first. Cheap (META section is small; we already cache).
        var allPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paths in _nsToPaths.Values)
            foreach (var p in paths) allPaths.Add(p);

        var modules    = new List<ExportedModule>();
        var packageOf  = new Dictionary<ExportedModule, string>();
        foreach (var path in allPaths)
        {
            var pkgName = GetPackageName(path);
            if (!activatedPackages.Contains(pkgName)) continue;
            foreach (var mod in LoadZpkg(path))
            {
                modules.Add(mod);
                packageOf[mod] = pkgName;
            }
        }
        return (modules, packageOf);
    }

    /// strict-using-resolution: enumerate all (path, packageName) pairs without
    /// triggering full TSIG decode. Reads META only.
    public IEnumerable<(string Path, string PackageName)> AllPackages()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paths in _nsToPaths.Values)
            foreach (var p in paths)
                if (seen.Add(p))
                    yield return (p, GetPackageName(p));
    }

    /// strict-using-resolution: which packages provide ANY type in the given
    /// namespace? Used to (a) verify `using` resolves, (b) compute activated set.
    public List<string> PackagesProvidingNamespace(string ns)
    {
        var result = new List<string>();
        if (!_nsToPaths.TryGetValue(ns, out var paths)) return result;
        foreach (var p in paths)
        {
            var pkg = GetPackageName(p);
            if (!result.Contains(pkg)) result.Add(pkg);
        }
        return result;
    }

    private string GetPackageName(string zpkgPath)
    {
        if (_pathToPkg.TryGetValue(zpkgPath, out var name)) return name;
        try
        {
            var bytes = File.ReadAllBytes(zpkgPath);
            var meta  = ZpkgReader.ReadMeta(bytes);
            _pathToPkg[zpkgPath] = meta.Name;
            return meta.Name;
        }
        catch
        {
            _pathToPkg[zpkgPath] = "";
            return "";
        }
    }

    private List<ExportedModule> LoadZpkg(string zpkgPath)
    {
        if (_cache.TryGetValue(zpkgPath, out var cached)) return cached;
        try
        {
            var bytes   = File.ReadAllBytes(zpkgPath);
            var meta    = ZpkgReader.ReadMeta(bytes);
            _pathToPkg[zpkgPath] = meta.Name;
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
