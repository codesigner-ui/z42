using Z42.Core.Diagnostics;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Pass 0: collects type shapes (enums, interfaces, classes, function signatures)
/// from a CompilationUnit into an immutable <see cref="SymbolTable"/>.
///
/// Does NOT bind expression bodies or default values — that is BodyBinder / TypeChecker's job.
/// </summary>
internal sealed partial class SymbolCollector : ISymbolBinder
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

    internal SymbolCollector(DiagnosticBag diags)
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
        CollectClasses(cu);
        CollectImpls(cu);
        CollectFunctions(cu);

        return new SymbolTable(
            _classes, _funcs, _interfaces,
            _globalEnumConstants, _enumTypes,
            _classInterfaces, _abstractMethods,
            _abstractClasses, _sealedClasses, _virtualMethods,
            _importedClassNames, _importedClassNamespaces);
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
            _funcs.TryAdd(name, ft);
        foreach (var (name, it) in imported.Interfaces)
            _interfaces.TryAdd(name, it);
        foreach (var (key, val) in imported.EnumConstants)
            _globalEnumConstants.TryAdd(key, val);
        foreach (var name in imported.EnumTypes)
            _enumTypes.Add(name);
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
            _interfaces[iface.Name] = new Z42InterfaceType(iface.Name, methods,
                StaticMembers: staticMembers.Count > 0 ? staticMembers : null);
            _activeTypeParams = null;
        }
    }

    // ── Pass 0c: class shapes — see SymbolCollector.Classes.cs ───────────────

    // ── Pass 0d: function signatures ──────────────────────────────────────────

    private void CollectFunctions(CompilationUnit cu)
    {
        foreach (var fn in cu.Functions)
        {
            if (_funcs.ContainsKey(fn.Name))
                _diags.Error(DiagnosticCodes.DuplicateDeclaration,
                    $"duplicate function declaration `{fn.Name}`", fn.Span);
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
            _                 => _enumTypes.Contains(nt.Name)                 ? new Z42EnumType(nt.Name)
                               : _classes.TryGetValue(nt.Name, out var ct)    ? (Z42Type)ct
                               : _interfaces.TryGetValue(nt.Name, out var it) ? it
                               : new Z42PrimType(nt.Name),
        },
        GenericType gt => gt.Name switch
        {
            "List"       => new Z42PrimType("List"),
            "Dictionary" => new Z42PrimType("Dictionary"),
            // L3-G2.5 chain: generic interface references preserve TypeArgs so
            // downstream arg-aware checks can compare `IEquatable<int>` precisely.
            _            => _classes.TryGetValue(gt.Name, out var ct) ? (Z42Type)ct
                          : _interfaces.TryGetValue(gt.Name, out var it)
                              ? (gt.TypeArgs.Count > 0
                                    ? new Z42InterfaceType(it.Name, it.Methods,
                                          gt.TypeArgs.Select(ResolveType).ToList())
                                    : it)
                          : new Z42PrimType(gt.Name),
        },
        _ => Z42Type.Unknown
    };

    // ── Signature building (no default binding) ──────────────────────────────

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
