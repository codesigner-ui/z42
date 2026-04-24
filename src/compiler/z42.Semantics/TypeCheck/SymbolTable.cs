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
    /// L3-G2.5 chain: per-class list of implemented interfaces with TypeArgs preserved.
    /// `class Foo: IEquatable<int>` stores `Z42InterfaceType("IEquatable", ..., [Int])`.
    public IReadOnlyDictionary<string, List<Z42InterfaceType>> ClassInterfaces { get; }
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
        Dictionary<string, List<Z42InterfaceType>> classInterfaces,
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
    /// Resolved constraints keyed by type-param name (L3-G2, L3-G2.5).
    /// Empty / missing = no constraint.
    private Dictionary<string, GenericConstraintBundle>? _activeTypeParamConstraints;

    /// Push type parameter names into scope for the duration of a generic body check.
    /// Optionally attach per-param constraints (base class + interfaces) for `where T: I + J` lookup.
    public void PushTypeParams(
        IEnumerable<string> typeParams,
        IReadOnlyDictionary<string, GenericConstraintBundle>? constraints = null)
    {
        _activeTypeParams = new HashSet<string>(typeParams);
        _activeTypeParamConstraints = constraints == null
            ? null
            : new Dictionary<string, GenericConstraintBundle>(constraints);
    }

    /// Clear type parameter scope.
    public void PopTypeParams()
    {
        _activeTypeParams = null;
        _activeTypeParamConstraints = null;
    }

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
                              => MakeTypeParam(nt.Name),
            _                 => TypeRegistry.GetZ42Type(nt.Name) ??
                               (EnumTypes.Contains(nt.Name)                  ? new Z42EnumType(nt.Name)
                               : Classes.TryGetValue(nt.Name, out var ct)    ? (Z42Type)ct
                               : Interfaces.TryGetValue(nt.Name, out var it) ? it
                               : new Z42PrimType(nt.Name)),
        },
        _ => Z42Type.Unknown
    };

    private Z42GenericParamType MakeTypeParam(string name)
    {
        var b = LookupActiveTypeParamConstraints(name);
        return new Z42GenericParamType(name, b.Interfaces.Count > 0 ? b.Interfaces : null, b.BaseClass);
    }

    /// Look up the active constraint bundle for a named type parameter.
    /// Used by body binding to "upgrade" a pre-resolved Z42GenericParamType (stored without
    /// constraints in Fields/Methods tables) to one carrying the current where-clause constraints.
    /// Returns `GenericConstraintBundle.Empty` when no constraints are active for this param.
    public GenericConstraintBundle LookupActiveTypeParamConstraints(string typeParam) =>
        _activeTypeParamConstraints != null && _activeTypeParamConstraints.TryGetValue(typeParam, out var b)
            ? b : GenericConstraintBundle.Empty;

    /// L3-G2.5 bare-typeparam: resolve U's effective constraint set including one hop
    /// through `TypeParamConstraint` (e.g. U: T means U also bears T's interfaces/base class
    /// for member-lookup purposes). Limited to a single hop to avoid cycles.
    public GenericConstraintBundle LookupEffectiveConstraints(string typeParam)
    {
        var b = LookupActiveTypeParamConstraints(typeParam);
        if (b.TypeParamConstraint is not { } upstream) return b;
        var up = LookupActiveTypeParamConstraints(upstream);
        var mergedIfaces = new List<Z42InterfaceType>(b.Interfaces);
        foreach (var i in up.Interfaces)
            if (!mergedIfaces.Any(x => x.Name == i.Name)) mergedIfaces.Add(i);
        return new GenericConstraintBundle(
            b.BaseClass ?? up.BaseClass,
            mergedIfaces,
            b.RequiresClass  || up.RequiresClass,
            b.RequiresStruct || up.RequiresStruct,
            b.TypeParamConstraint);
    }

    /// Resolve GenericType — user-defined generic classes / interfaces.
    /// (L3-G4a) For user-defined generics with matching type-arg arity, produce Z42InstantiatedType
    /// so member/method types can be substituted downstream. Falls back to bare Z42ClassType when
    /// type args are missing or arity mismatches (existing L3-G1 behaviour preserved).
    ///
    /// L3-G4h step3: pseudo-class `List<T>` / `Dictionary<K,V>` removed —— 现在 `new List<T>()`
    /// 路由到 stdlib `Std.Collections.List<T>` 源码类。旧的 `__list_*` / `__dict_*` VM builtin
    /// 不再由编译器发射。
    private Z42Type ResolveGenericType(GenericType gt)
    {
        // User-defined generic class: resolve as class type (code sharing — same class, different type_args)
        if (Classes.TryGetValue(gt.Name, out var ct))
        {
            if (ct.TypeParams is { Count: > 0 } tps && gt.TypeArgs.Count == tps.Count)
            {
                var resolvedArgs = gt.TypeArgs.Select(ResolveType).ToList();
                return new Z42InstantiatedType(ct, resolvedArgs);
            }
            return ct;
        }
        // L3-G2.5 chain: carry type args for generic interfaces so constraint
        // validation can verify `where T: IEquatable<U>` with cross-param substitution.
        if (Interfaces.TryGetValue(gt.Name, out var it))
        {
            if (gt.TypeArgs.Count == 0) return it;
            var resolvedArgs = gt.TypeArgs.Select(ResolveType).ToList();
            return new Z42InterfaceType(it.Name, it.Methods, resolvedArgs, it.StaticMembers);
        }
        return new Z42PrimType(gt.Name);
    }

    /// Query: is <paramref name="derived"/> a subclass of <paramref name="baseClass"/>?
    /// O(1) lookup using precomputed ancestor sets.
    public bool IsSubclassOf(string derived, string baseClass)
    {
        return _ancestors.TryGetValue(derived, out var ancestors) && ancestors.Contains(baseClass);
    }

    /// Query: does <paramref name="className"/> implement <paramref name="ifaceName"/>?
    /// Name-only match; walks base class chain. Kept for back-compat with pre-chain paths.
    public bool ImplementsInterface(string className, string ifaceName)
    {
        foreach (var _ in ImplementedInterfacesByName(className, ifaceName)) return true;
        return false;
    }

    /// L3-G2.5 chain: enumerate all declared interface instances on <paramref name="className"/>
    /// (walking up the base chain) whose name matches <paramref name="ifaceName"/>.
    /// Consumers can then compare TypeArgs for arg-aware checks.
    public IEnumerable<Z42InterfaceType> ImplementedInterfacesByName(string className, string ifaceName)
    {
        var cur = className;
        while (cur != null)
        {
            if (ClassInterfaces.TryGetValue(cur, out var ifaces))
                foreach (var it in ifaces)
                    if (it.Name == ifaceName) yield return it;
            cur = Classes.TryGetValue(cur, out var ct) ? ct.BaseClassName : null;
        }
    }
}
