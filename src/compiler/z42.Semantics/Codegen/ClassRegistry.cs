using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

/// <summary>
/// Unified registry of class metadata for code generation.
/// Replaces the four parallel dictionaries that were previously maintained
/// separately in <see cref="IrGen"/> (classMethods, classStaticMethods,
/// classInstanceFields, classBaseNames).
/// </summary>
internal sealed class ClassRegistry
{
    private readonly Dictionary<string, ClassEntry> _entries = new(StringComparer.Ordinal);

    internal sealed class ClassEntry
    {
        public HashSet<string> Methods       { get; } = new(StringComparer.Ordinal);
        public HashSet<string> StaticMethods { get; } = new(StringComparer.Ordinal);
        public HashSet<string> InstanceFields { get; } = new(StringComparer.Ordinal);
        public string? BaseClassName { get; set; }
    }

    /// Registers a class from the SemanticModel.
    internal void Register(string qualName, Z42ClassType ct, string? qualBaseName)
    {
        var entry = GetOrCreate(qualName);
        entry.Methods.UnionWith(ct.Methods.Keys);
        entry.StaticMethods.UnionWith(ct.StaticMethods.Keys);
        entry.InstanceFields.UnionWith(ct.Fields.Keys);
        entry.BaseClassName = qualBaseName;
    }

    internal bool TryGetMethods(string qualClass, out HashSet<string> methods)
    {
        if (_entries.TryGetValue(qualClass, out var e)) { methods = e.Methods; return true; }
        methods = null!; return false;
    }

    internal bool TryGetStaticMethods(string qualClass, out HashSet<string> methods)
    {
        if (_entries.TryGetValue(qualClass, out var e)) { methods = e.StaticMethods; return true; }
        methods = null!; return false;
    }

    internal bool TryGetInstanceFields(string qualClass, out HashSet<string> fields)
    {
        if (_entries.TryGetValue(qualClass, out var e)) { fields = e.InstanceFields; return true; }
        fields = null!; return false;
    }

    internal bool TryGetBaseClassName(string qualClass, out string? baseName)
    {
        if (_entries.TryGetValue(qualClass, out var e)) { baseName = e.BaseClassName; return true; }
        baseName = null; return false;
    }

    /// Returns instance field names for a class, including all inherited fields.
    internal HashSet<string> GetAllInstanceFields(string qualClass)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        string? current = qualClass;
        while (current is not null)
        {
            if (_entries.TryGetValue(current, out var e))
            {
                result.UnionWith(e.InstanceFields);
                current = e.BaseClassName;
            }
            else break;
        }
        return result;
    }

    /// Finds the qualified method key for a virtual call default expansion.
    internal string? FindVcallParamsKey(
        string methodName, int suppliedArgCount,
        Dictionary<string, IReadOnlyList<Syntax.Parser.Param>> funcParams)
    {
        foreach (var (cls, entry) in _entries)
        {
            if (!entry.Methods.Contains(methodName)) continue;
            string key = $"{cls}.{methodName}";
            if (funcParams.TryGetValue(key, out var parms) && parms.Count > suppliedArgCount)
                return key;
        }
        return null;
    }

    private ClassEntry GetOrCreate(string qualName)
    {
        if (!_entries.TryGetValue(qualName, out var entry))
        {
            entry = new ClassEntry();
            _entries[qualName] = entry;
        }
        return entry;
    }
}
