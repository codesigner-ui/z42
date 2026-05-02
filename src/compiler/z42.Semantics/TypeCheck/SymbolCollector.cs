using Z42.Core.Diagnostics;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Pass 0: collects type shapes (enums, interfaces, classes, function signatures)
/// from a CompilationUnit into an immutable <see cref="SymbolTable"/>.
///
/// Does NOT bind expression bodies or default values — that is BodyBinder / TypeChecker's job.
/// </summary>
public sealed partial class SymbolCollector : ISymbolBinder
{
    private readonly DiagnosticBag _diags;

    // Mutable state populated during collection, frozen into SymbolTable at the end.
    internal readonly Dictionary<string, Z42FuncType>      _funcs      = new();
    internal readonly Dictionary<string, Z42ClassType>     _classes    = new();
    internal readonly Dictionary<string, Z42InterfaceType> _interfaces = new();
    internal readonly Dictionary<string, long>             _globalEnumConstants = new();
    internal readonly HashSet<string>                      _enumTypes           = new();
    internal readonly Dictionary<string, List<Z42InterfaceType>> _classInterfaces = new();
    internal readonly Dictionary<string, HashSet<string>>  _abstractMethods = new();
    internal readonly HashSet<string>                      _abstractClasses = new();
    internal readonly HashSet<string>                      _sealedClasses   = new();
    internal readonly Dictionary<string, HashSet<string>>  _virtualMethods  = new();
    internal readonly HashSet<string>                      _importedClassNames = new();
    internal Dictionary<string, string>                    _importedClassNamespaces = new();
    /// 跟踪 imported (来自 zpkg) interface / func / enum names，用于
    /// `SymbolTable.ExtractIntraSymbols()` 区分本包 declarations。
    /// (与 _importedClassNames 对称。fix-package-compiler-cross-file 引入。)
    internal readonly HashSet<string>                      _importedInterfaceNames = new();
    internal readonly HashSet<string>                      _importedFuncNames      = new();
    internal readonly HashSet<string>                      _importedEnumNames      = new();
    /// 2026-05-02 add-delegate-type: 用户声明的 delegate 注册表（含顶层 + 嵌套）。
    /// Key = "Foo" / "Foo$N" / "Btn.OnClick" / "Btn.OnClick$N"。
    internal readonly Dictionary<string, DelegateInfo>     _delegates              = new();
    /// D1c: keys of delegates that came from imported zpkg TSIG. Local
    /// re-registration with the same key (intra-package self-import or stdlib
    /// rebuild seeing its own cached TSIG) silently overrides imported entry
    /// instead of reporting a duplicate-decl error.
    internal readonly HashSet<string>                      _importedDelegateKeys   = new();

    public SymbolCollector(DiagnosticBag diags)
    {
        _diags = diags;
    }

    /// <summary>
    /// Run all collection passes and return a frozen SymbolTable.
    /// </summary>
    public SymbolTable Collect(CompilationUnit cu, ImportedSymbols? imported = null)
    {
        // Merge imported symbols before local collection (locals override imports).
        if (imported != null)
            MergeImported(imported);

        CollectEnums(cu);
        CollectInterfaces(cu);
        CollectDelegates(cu);   // 2026-05-02 add-delegate-type — must precede CollectClasses（class 字段类型可能引用 delegate）
        CollectClasses(cu);
        CollectImpls(cu);
        CollectFunctions(cu);

        return new SymbolTable(
            _classes, _funcs, _interfaces,
            _globalEnumConstants, _enumTypes,
            _classInterfaces, _abstractMethods,
            _abstractClasses, _sealedClasses, _virtualMethods,
            _importedClassNames, _importedClassNamespaces,
            _importedInterfaceNames, _importedFuncNames, _importedEnumNames,
            delegates: _delegates);
    }

    /// 2026-05-02 add-delegate-type: 收集顶层 + 嵌套 delegate。
    /// 顶层 → "Foo" / "Foo$N"；嵌套 → "Class.Foo" / "Class.Foo$N"。
    /// 解析签名时若 delegate 含 type-params，激活 _activeTypeParams 让
    /// ResolveType 把 `T` 解析为 Z42GenericParamType 占位（与 generic class
    /// / func 同款 pattern）。
    private void CollectDelegates(CompilationUnit cu)
    {
        if (cu.Delegates is { } topLevels)
            foreach (var d in topLevels)
                RegisterDelegate(d, containerClass: null);

        foreach (var cls in cu.Classes)
            if (cls.NestedDelegates is { } nested)
                foreach (var d in nested)
                    RegisterDelegate(d, containerClass: cls.Name);
    }

    private void RegisterDelegate(DelegateDecl d, string? containerClass)
    {
        var prev = _activeTypeParams;
        _activeTypeParams = d.TypeParams != null
            ? new HashSet<string>(d.TypeParams)
            : null;
        try
        {
            var paramTypes = d.Params.Select(p => ResolveType(p.Type)).ToList();
            var retType    = ResolveType(d.ReturnType);
            var sig        = new Z42FuncType(paramTypes, retType);
            var info       = new DelegateInfo(sig, d.TypeParams ?? [], containerClass);

            var arity = d.TypeParams?.Count ?? 0;
            // 简单名 key —— 类内部 + 顶层都可直接 `OnClick` 引用。同名全局 delegate
            // / 同名嵌套 delegate 之间冲突属于 v1 限制（和 D1a Open Question 一致）。
            var simpleKey = arity > 0 ? $"{d.Name}${arity}" : d.Name;
            // D1c: 如果该 key 已存在但来自 imported TSIG（自我导入 / stdlib 重建
            // 看到自己的 cached TSIG），允许 local declaration 覆盖；不报错。
            // 来自其他 local delegate 的真正冲突仍然报错。
            if (!_delegates.TryAdd(simpleKey, info))
            {
                if (_importedDelegateKeys.Remove(simpleKey))
                    _delegates[simpleKey] = info; // override imported with local
                else
                    _diags.Error(DiagnosticCodes.DuplicateDeclaration,
                        $"delegate `{simpleKey}` is already declared", d.Span);
            }

            // 嵌套 delegate 同时注册 qualified key（"Btn.OnClick" / "Btn.OnClick$N"），
            // 为未来 dotted-path 外部引用预留；当前 ResolveType 仅消费 simpleKey。
            if (containerClass is not null)
            {
                var qualifiedKey = arity > 0
                    ? $"{containerClass}.{d.Name}${arity}"
                    : $"{containerClass}.{d.Name}";
                if (!_delegates.TryAdd(qualifiedKey, info))
                {
                    if (_importedDelegateKeys.Remove(qualifiedKey))
                        _delegates[qualifiedKey] = info;
                    // 嵌套 qualified 路径不报 duplicate（D1a 已存在 silent 行为）
                }
            }
        }
        finally { _activeTypeParams = prev; }
    }

    /// Merge imported symbols from dependency zpkgs.
    /// Imported classes are added to _classes so TypeChecker can resolve methods/fields.
    /// ImportedClassNames tracks which classes came from imports (for lenient arg checking
    /// and correct namespace qualification in IrGen).
    private void MergeImported(ImportedSymbols imported)
    {
        foreach (var (name, ct) in imported.Classes)
        {
            if (_classes.TryAdd(name, ct))
                _importedClassNames.Add(name);
        }
        foreach (var (name, ft) in imported.Functions)
        {
            if (_funcs.TryAdd(name, ft))
                _importedFuncNames.Add(name);
        }
        foreach (var (name, it) in imported.Interfaces)
        {
            if (_interfaces.TryAdd(name, it))
                _importedInterfaceNames.Add(name);
        }
        foreach (var (key, val) in imported.EnumConstants)
            _globalEnumConstants.TryAdd(key, val);
        foreach (var name in imported.EnumTypes)
        {
            if (_enumTypes.Add(name))
                _importedEnumNames.Add(name);
        }
        // 2026-05-02 add-generic-delegates (D1c): import imported delegate registry
        if (imported.Delegates is { } importedDelegates)
            foreach (var (key, info) in importedDelegates)
            {
                if (_delegates.TryAdd(key, info))
                    _importedDelegateKeys.Add(key);
            }

        foreach (var (name, ns) in imported.ClassNamespaces)
            _importedClassNamespaces.TryAdd(name, ns);
        // L3-G4b primitive-as-struct: import stdlib `struct int : IComparable<int>` etc.
        // into the classInterfaces registry so `PrimitiveImplementsInterface` can answer
        // "int satisfies IComparable" by reading TSIG instead of a hardcoded switch.
        if (imported.ClassInterfaces is { } ciMap)
            foreach (var (name, ifaceNames) in ciMap)
            {
                if (_classInterfaces.ContainsKey(name)) continue;
                var list = new List<Z42InterfaceType>(ifaceNames.Count);
                foreach (var ifaceName in ifaceNames)
                    if (_interfaces.TryGetValue(ifaceName, out var it))
                        list.Add(it);
                    else
                        list.Add(new Z42InterfaceType(ifaceName,
                            new Dictionary<string, Z42FuncType>()));
                _classInterfaces[name] = list;
            }
    }

    // ── Pass 0a: enum constants ───────────────────────────────────────────────

    private void CollectEnums(CompilationUnit cu)
    {
        foreach (var en in cu.Enums)
        {
            foreach (var m in en.Members)
                _globalEnumConstants[$"{en.Name}.{m.Name}"] = m.Value ?? 0;
            _enumTypes.Add(en.Name);
        }
    }

    // ── Pass 0b: interface shapes ─────────────────────────────────────────────

    private void CollectInterfaces(CompilationUnit cu)
    {
        foreach (var iface in cu.Interfaces)
        {
            // Activate the interface's own type params so `T` inside method signatures
            // resolves to Z42GenericParamType instead of falling back to Z42PrimType("T").
            if (iface.TypeParams is { Count: > 0 } tps)
                _activeTypeParams = new HashSet<string>(tps);
            var methods       = new Dictionary<string, Z42FuncType>();
            var staticMembers = new Dictionary<string, Z42StaticMember>();
            foreach (var m in iface.Methods)
            {
                var sig = BuildFuncSignature(m.Params, ResolveType(m.ReturnType));
                if (m.IsStatic)
                {
                    // Tier derivation: Parser already validated abstract↔no-body /
                    // virtual↔body combos. So here:
                    //   IsVirtual=true → Virtual (body present)
                    //   Body=null      → Abstract
                    //   otherwise      → Concrete (sealed)
                    var kind = m.IsVirtual ? StaticMemberKind.Virtual
                             : m.Body is null ? StaticMemberKind.Abstract
                             : StaticMemberKind.Concrete;
                    staticMembers[m.Name] = new Z42StaticMember(m.Name, sig, kind);
                }
                else
                {
                    methods[m.Name] = sig;
                }
            }
            var ifaceTpList = iface.TypeParams is { Count: > 0 } iTps ? iTps.AsReadOnly() : null;
            _interfaces[iface.Name] = new Z42InterfaceType(iface.Name, methods,
                StaticMembers: staticMembers.Count > 0 ? staticMembers : null,
                TypeParams:    ifaceTpList);
            _activeTypeParams = null;
        }
    }

    // ── Pass 0c: class shapes — see SymbolCollector.Classes.cs ───────────────

    // ── Pass 0d: function signatures ──────────────────────────────────────────

    private void CollectFunctions(CompilationUnit cu)
    {
        foreach (var fn in cu.Functions)
        {
            // L3-Impl2 / cross-CU Phase 1: a local function name may already be in
            // `_funcs` because it was injected via `MergeImported` from intraSymbols
            // (same package's pre-collected declarations). In that case, the local
            // declaration shadows the import — drop the imported entry silently and
            // fall through to the normal registration. Only error on **local-local**
            // collisions (same name declared twice within the local source).
            if (_funcs.ContainsKey(fn.Name))
            {
                if (_importedFuncNames.Remove(fn.Name))
                    _funcs.Remove(fn.Name); // shed the import; about to re-add as local
                else
                    _diags.Error(DiagnosticCodes.DuplicateDeclaration,
                        $"duplicate function declaration `{fn.Name}`", fn.Span);
            }
            if (fn.TypeParams != null) _activeTypeParams = new HashSet<string>(fn.TypeParams);
            _funcs[fn.Name] = BuildFuncSignature(fn.Params, ResolveType(fn.ReturnType));
            _activeTypeParams = null;
        }
    }

    /// Active generic type parameters — set during signature collection so T resolves to Z42GenericParamType.
    private HashSet<string>? _activeTypeParams;

    // ── Type resolution (collection phase) ───────────────────────────────────

    /// Resolve a TypeExpr to a Z42Type using the currently-collected classes/interfaces.
    internal Z42Type ResolveType(TypeExpr typeExpr) => typeExpr switch
    {
        VoidType      => Z42Type.Void,
        OptionType ot => new Z42OptionType(ResolveType(ot.Inner)),
        ArrayType  at => new Z42ArrayType(ResolveType(at.Element)),
        // Function type `(T1, T2) -> R` — see docs/design/closure.md §3.2
        FuncType   ft => new Z42FuncType(
                            ft.ParamTypes.Select(ResolveType).ToList(),
                            ResolveType(ft.ReturnType)),
        NamedType  nt when _activeTypeParams?.Contains(nt.Name) == true
                      => new Z42GenericParamType(nt.Name),
        NamedType  nt => nt.Name switch
        {
            "int"    or "i32" => Z42Type.Int,
            "long"   or "i64" => Z42Type.Long,
            "float"  or "f32" => Z42Type.Float,
            "double" or "f64" => Z42Type.Double,
            "bool"            => Z42Type.Bool,
            "string"          => Z42Type.String,
            "char"            => Z42Type.Char,
            "object"          => Z42Type.Object,
            "void"            => Z42Type.Void,
            "var"             => Z42Type.Unknown,
            "i8"              => Z42Type.I8,
            "i16"             => Z42Type.I16,
            "u8"              => Z42Type.U8,
            "u16"             => Z42Type.U16,
            "u32"             => Z42Type.U32,
            "u64"             => Z42Type.U64,
            "sbyte"           => Z42Type.I8,
            "short"           => Z42Type.I16,
            "byte"            => Z42Type.U8,
            "ushort"          => Z42Type.U16,
            "uint"            => Z42Type.U32,
            "ulong"           => Z42Type.U64,
            // 2026-05-02 add-delegate-type: 用户声明的非泛型 delegate → Z42FuncType
            _ when _delegates.TryGetValue(nt.Name, out var di) && di.TypeParams.Count == 0
                              => di.Signature,
            _                 => _enumTypes.Contains(nt.Name)                 ? new Z42EnumType(nt.Name)
                               : _classes.TryGetValue(nt.Name, out var ct)    ? (Z42Type)ct
                               : _interfaces.TryGetValue(nt.Name, out var it) ? it
                               : new Z42PrimType(nt.Name),
        },
        GenericType gt when _delegates.TryGetValue($"{gt.Name}${gt.TypeArgs.Count}", out var dgi)
                            && dgi.TypeParams.Count == gt.TypeArgs.Count
                       => InstantiateGenericDelegate(dgi, gt.TypeArgs.Select(ResolveType).ToList()),
        GenericType gt => gt.Name switch
        {
            "List"       => new Z42PrimType("List"),
            "Dictionary" => new Z42PrimType("Dictionary"),
            // L3-G2.5 chain: generic interface references preserve TypeArgs so
            // downstream arg-aware checks can compare `IEquatable<int>` precisely.
            // L3 static abstract (C# 11): also preserve StaticMembers so generic
            // constraint references like `where T: INumber<T>` keep access to
            // the interface's static abstract / virtual members.
            // 2026-04-28 fix-generic-type-roundtrip: 同样为 generic 类引用保留
            // TypeArgs，构造 Z42InstantiatedType。否则 `KeyValuePair<K, V>` 在
            // SymbolCollector 阶段被退化为 bare Z42ClassType，TSIG 序列化丢失
            // type-args，消费端 `dict.Entries()[m].Value` 拿到 generic param V。
            _            => _classes.TryGetValue(gt.Name, out var ct)
                              ? (gt.TypeArgs.Count > 0 && ct.TypeParams is { Count: > 0 } tps
                                  && gt.TypeArgs.Count == tps.Count
                                    ? (Z42Type)new Z42InstantiatedType(ct,
                                          gt.TypeArgs.Select(ResolveType).ToList())
                                    : (Z42Type)ct)
                          : _interfaces.TryGetValue(gt.Name, out var it)
                              ? (gt.TypeArgs.Count > 0
                                    ? new Z42InterfaceType(it.Name, it.Methods,
                                          gt.TypeArgs.Select(ResolveType).ToList(),
                                          it.StaticMembers, it.TypeParams)
                                    : it)
                          : new Z42PrimType(gt.Name),
        },
        _ => Z42Type.Unknown
    };

    // ── Signature building (no default binding) ──────────────────────────────

    /// 2026-05-02 add-delegate-type: generic delegate 实例化。把 DelegateInfo
    /// 中的 Z42GenericParamType 占位（"T" / "R" 等）按对应类型参数下标替换为
    /// 具体类型。复用 `TypeChecker.SubstituteTypeParams`（同 generic class / func
    /// 路径）。
    private Z42Type InstantiateGenericDelegate(DelegateInfo info, IReadOnlyList<Z42Type> typeArgs)
    {
        var subMap = new Dictionary<string, Z42Type>(info.TypeParams.Count);
        for (int i = 0; i < info.TypeParams.Count; i++)
            subMap[info.TypeParams[i]] = typeArgs[i];
        return TypeChecker.SubstituteTypeParams(info.Signature, subMap);
    }

    /// Build a Z42FuncType from parameter types + return type.
    /// Computes RequiredCount from `p.Default != null` — does NOT bind default expressions.
    internal Z42FuncType BuildFuncSignature(IReadOnlyList<Param> parms, Z42Type retType)
    {
        var paramTypes    = parms.Select(p => ResolveType(p.Type)).ToList();
        int requiredCount = parms.Count;
        for (int i = 0; i < parms.Count; i++)
        {
            if (parms[i].Default != null)
            {
                if (i < requiredCount) requiredCount = i;
            }
            else if (i >= requiredCount)
            {
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"non-default parameter `{parms[i].Name}` follows a default parameter (parameter ordering)",
                    parms[i].Span);
            }
        }
        return new Z42FuncType(paramTypes, retType, requiredCount == parms.Count ? -1 : requiredCount);
    }
}
