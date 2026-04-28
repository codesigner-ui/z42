using Z42.IR;
using Z42.Project;

namespace Z42.Pipeline;

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
