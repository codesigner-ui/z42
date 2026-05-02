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

    /// 2026-05-02 add-delegate-type: 命名 delegate 注册表。
    /// Key 形式：
    ///   非泛型顶层  →  "Foo"
    ///   泛型顶层    →  "Foo$N"  （N = TypeParams.Count）
    ///   嵌套        →  "Btn.OnClick"  或  "Btn.OnClick$N"
    /// Value 携带签名 / 类型参数 / 可选 where 约束元数据。
    public IReadOnlyDictionary<string, DelegateInfo> Delegates { get; }
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

    /// Names of interfaces / functions / enums imported from dependency zpkgs.
    /// 与 ImportedClassNames 对称；用于 `ExtractIntraSymbols` 区分本包 declarations.
    /// (fix-package-compiler-cross-file 引入。)
    public IReadOnlySet<string> ImportedInterfaceNames { get; }
    public IReadOnlySet<string> ImportedFuncNames      { get; }
    public IReadOnlySet<string> ImportedEnumNames      { get; }

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
        Dictionary<string, string>? importedClassNamespaces = null,
        HashSet<string>? importedInterfaceNames = null,
        HashSet<string>? importedFuncNames = null,
        HashSet<string>? importedEnumNames = null,
        Dictionary<string, DelegateInfo>? delegates = null)
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
        ImportedInterfaceNames  = importedInterfaceNames  ?? new HashSet<string>();
        ImportedFuncNames       = importedFuncNames       ?? new HashSet<string>();
        ImportedEnumNames       = importedEnumNames       ?? new HashSet<string>();
        Delegates               = delegates ?? new Dictionary<string, DelegateInfo>();
        _ancestors = BuildAncestorSets(classes);
    }

    /// 抽出本包内（非 imported）declarations，返回 ImportedSymbols 形式。
    /// 用于 PackageCompiler 多 CU 编译时把同包内每个 cu 的 Pass-0 收集结果
    /// 合并成 intraSymbols，供 Phase 2 完整编译时作为额外 imported 注入。
    /// (fix-package-compiler-cross-file 引入。)
    ///
    /// 2026-04-28 fix-intra-package-namespace：包内多 CU 各自带不同 namespace
    /// （如 z42.core 同时含 `Std`、`Std.Collections`、`Std.IO`），原先把所有
    /// class 都标成首个 CU 的 namespace 会导致跨 namespace 的同包引用走错前缀
    /// （如 Dictionary.Entries() 内 `new KeyValuePair<K,V>(...)` 被发射成
    /// `Std.KeyValuePair` 而非 `Std.Collections.KeyValuePair`，运行期找不到
    /// 类型，构造器不写字段，`.Value` 全是 null）。修复：传入 per-class
    /// namespace map，缺省时回退到 namespaceName。
    public ImportedSymbols ExtractIntraSymbols(
        string namespaceName,
        IReadOnlyDictionary<string, string>? classNamespaces = null)
    {
        var classes    = new Dictionary<string, Z42ClassType>(StringComparer.Ordinal);
        var classNs    = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, ct) in Classes)
        {
            if (ImportedClassNames.Contains(name)) continue;
            if (name == "Object") continue; // synthetic stub
            classes[name] = ct;
            classNs[name] = classNamespaces is not null
                            && classNamespaces.TryGetValue(name, out var perClassNs)
                ? perClassNs : namespaceName;
        }

        var interfaces = new Dictionary<string, Z42InterfaceType>(StringComparer.Ordinal);
        foreach (var (name, it) in Interfaces)
        {
            if (ImportedInterfaceNames.Contains(name)) continue;
            interfaces[name] = it;
        }

        var funcs = new Dictionary<string, Z42FuncType>(StringComparer.Ordinal);
        foreach (var (name, ft) in Functions)
        {
            if (ImportedFuncNames.Contains(name)) continue;
            funcs[name] = ft;
        }

        var enumTypes  = new HashSet<string>(StringComparer.Ordinal);
        var enumConsts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var t in EnumTypes)
        {
            if (ImportedEnumNames.Contains(t)) continue;
            enumTypes.Add(t);
        }
        foreach (var (key, val) in EnumConstants)
        {
            int dot = key.IndexOf('.');
            if (dot < 0) continue;
            var en = key[..dot];
            if (enumTypes.Contains(en)) enumConsts[key] = val;
        }

        // ClassInterfaces: 仅含本包内 class 的 interface 列表（按短名）
        var classInterfaces = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (className, ifList) in ClassInterfaces)
        {
            if (ImportedClassNames.Contains(className)) continue;
            classInterfaces[className] = ifList.Select(i => i.Name).ToList();
        }

        return new ImportedSymbols(
            Classes:          classes,
            Functions:        funcs,
            Interfaces:       interfaces,
            EnumConstants:    enumConsts,
            EnumTypes:        enumTypes,
            ClassNamespaces:  classNs,
            ClassConstraints: null,    // intra-package 不传约束（local 会重新 collect）
            FuncConstraints:  null,
            ClassInterfaces:  classInterfaces);
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
        // Function type `(T1, T2) -> R` — see docs/design/closure.md §3.2
        FuncType   ft => new Z42FuncType(
                            ft.ParamTypes.Select(ResolveType).ToList(),
                            ResolveType(ft.ReturnType)),
        GenericType gt => ResolveGenericType(gt),
        NamedType  nt => nt.Name switch
        {
            "var"             => Z42Type.Unknown,
            _ when _activeTypeParams?.Contains(nt.Name) == true
                              => MakeTypeParam(nt.Name),
            // 2026-05-02 add-delegate-type: 命名 delegate（非泛型）→ Z42FuncType
            _ when Delegates.TryGetValue(nt.Name, out var di) && di.TypeParams.Count == 0
                              => di.Signature,
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
        // 2026-05-02 add-delegate-type: 用户声明的泛型 delegate 优先于 hardcoded
        // `Func`/`Action` desugar 路径 —— 当用户在 stdlib 或 CU 内定义同名
        // delegate 时直接走 SymbolTable 路径。Hardcoded 路径在 D1c 移除。
        var arityKey = $"{gt.Name}${gt.TypeArgs.Count}";
        if (Delegates.TryGetValue(arityKey, out var di) && di.TypeParams.Count == gt.TypeArgs.Count)
        {
            var resolvedArgs = gt.TypeArgs.Select(ResolveType).ToList();
            var subMap = new Dictionary<string, Z42Type>(di.TypeParams.Count);
            for (int i = 0; i < di.TypeParams.Count; i++)
                subMap[di.TypeParams[i]] = resolvedArgs[i];
            return TypeChecker.SubstituteTypeParams(di.Signature, subMap);
        }

        // 2026-05-02 add-generic-delegates (D1c): hardcoded `Action`/`Func`
        // desugar removed — replaced by stdlib `Std.Delegates`（z42.core 内
        // 真实 delegate 声明）+ TSIG export 路径。

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
            return new Z42InterfaceType(it.Name, it.Methods, resolvedArgs,
                it.StaticMembers, it.TypeParams);
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
