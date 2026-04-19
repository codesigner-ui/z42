using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Readonly snapshot of all type shapes collected during Pass 0.
/// Serves as the explicit data boundary between symbol collection and body binding.
///
/// Future: when TypeChecker is fully split, SymbolCollector produces this,
/// and BodyBinder consumes it without access to mutable collection state.
/// </summary>
public sealed class SymbolTable
{
    public IReadOnlyDictionary<string, Z42ClassType> Classes { get; }
    public IReadOnlyDictionary<string, Z42FuncType> Functions { get; }
    public IReadOnlyDictionary<string, Z42InterfaceType> Interfaces { get; }
    public IReadOnlyDictionary<string, long> EnumConstants { get; }
    public IReadOnlySet<string> EnumTypes { get; }
    public IReadOnlyDictionary<string, HashSet<string>> ClassInterfaces { get; }
    public IReadOnlyDictionary<string, HashSet<string>> AbstractMethods { get; }
    public IReadOnlySet<string> AbstractClasses { get; }
    public IReadOnlySet<string> SealedClasses { get; }
    public IReadOnlyDictionary<string, HashSet<string>> VirtualMethods { get; }

    /// Names of classes that were imported from dependency zpkgs (not locally defined).
    public IReadOnlySet<string> ImportedClassNames { get; }

    /// Maps imported class short name → its original namespace (e.g. "Console" → "Std.IO").
    /// Used by IrGen to qualify imported class calls with the correct dependency namespace.
    public IReadOnlyDictionary<string, string> ImportedClassNamespaces { get; }

    /// Precomputed ancestor sets: for each class, the set of all its ancestor class names.
    /// Enables O(1) subclass checks instead of walking the inheritance chain.
    private readonly Dictionary<string, HashSet<string>> _ancestors;

    internal SymbolTable(
        Dictionary<string, Z42ClassType> classes,
        Dictionary<string, Z42FuncType> functions,
        Dictionary<string, Z42InterfaceType> interfaces,
        Dictionary<string, long> enumConstants,
        HashSet<string> enumTypes,
        Dictionary<string, HashSet<string>> classInterfaces,
        Dictionary<string, HashSet<string>> abstractMethods,
        HashSet<string> abstractClasses,
        HashSet<string> sealedClasses,
        Dictionary<string, HashSet<string>> virtualMethods,
        HashSet<string>? importedClassNames = null,
        Dictionary<string, string>? importedClassNamespaces = null)
    {
        Classes = classes;
        Functions = functions;
        Interfaces = interfaces;
        EnumConstants = enumConstants;
        EnumTypes = enumTypes;
        ClassInterfaces = classInterfaces;
        AbstractMethods = abstractMethods;
        AbstractClasses = abstractClasses;
        SealedClasses = sealedClasses;
        VirtualMethods = virtualMethods;
        ImportedClassNames = importedClassNames ?? new HashSet<string>();
        ImportedClassNamespaces = importedClassNamespaces ?? new Dictionary<string, string>();
        _ancestors = BuildAncestorSets(classes);
    }

    /// Build ancestor set for each class by walking the inheritance chain once per class.
    private static Dictionary<string, HashSet<string>> BuildAncestorSets(
        Dictionary<string, Z42ClassType> classes)
    {
        var result = new Dictionary<string, HashSet<string>>(classes.Count);
        foreach (var name in classes.Keys)
            GetOrBuildAncestors(name, classes, result);
        return result;
    }

    private static HashSet<string> GetOrBuildAncestors(
        string name,
        Dictionary<string, Z42ClassType> classes,
        Dictionary<string, HashSet<string>> cache)
    {
        if (cache.TryGetValue(name, out var cached)) return cached;
        var ancestors = new HashSet<string>();
        cache[name] = ancestors; // store early to handle cycles
        if (classes.TryGetValue(name, out var ct) && ct.BaseClassName is { } baseName)
        {
            ancestors.Add(baseName);
            // Merge parent's ancestors (recursively built if needed)
            var parentAncestors = GetOrBuildAncestors(baseName, classes, cache);
            ancestors.UnionWith(parentAncestors);
        }
        return ancestors;
    }

    // ── Active type parameters (set by TypeChecker during generic body checking) ──

    private HashSet<string>? _activeTypeParams;

    /// Push type parameter names into scope for the duration of a generic body check.
    public void PushTypeParams(IEnumerable<string> typeParams) =>
        _activeTypeParams = new HashSet<string>(typeParams);

    /// Clear type parameter scope.
    public void PopTypeParams() => _activeTypeParams = null;

    /// Resolve a TypeExpr to a Z42Type using the frozen symbol table.
    public Z42Type ResolveType(TypeExpr typeExpr) => typeExpr switch
    {
        VoidType      => Z42Type.Void,
        OptionType ot => new Z42OptionType(ResolveType(ot.Inner)),
        ArrayType  at => new Z42ArrayType(ResolveType(at.Element)),
        GenericType gt => ResolveGenericType(gt),
        NamedType  nt => nt.Name switch
        {
            "var"             => Z42Type.Unknown,
            _ when _activeTypeParams?.Contains(nt.Name) == true
                              => new Z42GenericParamType(nt.Name),
            _                 => TypeRegistry.GetZ42Type(nt.Name) ??
                               (Classes.TryGetValue(nt.Name, out var ct)    ? (Z42Type)ct
                               : Interfaces.TryGetValue(nt.Name, out var it) ? it
                               : new Z42PrimType(nt.Name)),
        },
        _ => Z42Type.Unknown
    };

    /// Resolve GenericType — handles pseudo-class (List<T>, Dictionary<K,V>) and user-defined generics.
    private Z42Type ResolveGenericType(GenericType gt)
    {
        // Pseudo-class compatibility: List<T> and Dictionary<K,V> map to built-in types
        if (gt.Name is "List") return TypeRegistry.GetZ42Type("List") ?? new Z42PrimType("List");
        if (gt.Name is "Dictionary") return TypeRegistry.GetZ42Type("Dictionary") ?? new Z42PrimType("Dictionary");

        // User-defined generic class: resolve as class type (code sharing — same class, different type_args)
        if (Classes.TryGetValue(gt.Name, out var ct)) return ct;
        if (Interfaces.TryGetValue(gt.Name, out var it)) return it;
        return new Z42PrimType(gt.Name);
    }

    /// Query: is <paramref name="derived"/> a subclass of <paramref name="baseClass"/>?
    /// O(1) lookup using precomputed ancestor sets.
    public bool IsSubclassOf(string derived, string baseClass)
    {
        return _ancestors.TryGetValue(derived, out var ancestors) && ancestors.Contains(baseClass);
    }

    /// Query: does <paramref name="className"/> implement <paramref name="ifaceName"/>?
    public bool ImplementsInterface(string className, string ifaceName)
    {
        var cur = className;
        while (cur != null)
        {
            if (ClassInterfaces.TryGetValue(cur, out var ifaces) && ifaces.Contains(ifaceName))
                return true;
            cur = Classes.TryGetValue(cur, out var ct) ? ct.BaseClassName : null;
        }
        return false;
    }
}
