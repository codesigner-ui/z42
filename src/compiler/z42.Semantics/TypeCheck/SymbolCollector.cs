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
    internal readonly Dictionary<string, HashSet<string>>  _classInterfaces = new();
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
            var methods = new Dictionary<string, Z42FuncType>();
            foreach (var m in iface.Methods)
                methods[m.Name] = BuildFuncSignature(m.Params, ResolveType(m.ReturnType));
            _interfaces[iface.Name] = new Z42InterfaceType(iface.Name, methods);
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
            _funcs[fn.Name] = BuildFuncSignature(fn.Params, ResolveType(fn.ReturnType));
        }
    }

    // ── Type resolution (collection phase) ───────────────────────────────────

    /// Resolve a TypeExpr to a Z42Type using the currently-collected classes/interfaces.
    internal Z42Type ResolveType(TypeExpr typeExpr) => typeExpr switch
    {
        VoidType      => Z42Type.Void,
        OptionType ot => new Z42OptionType(ResolveType(ot.Inner)),
        ArrayType  at => new Z42ArrayType(ResolveType(at.Element)),
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
            _                 => _classes.TryGetValue(nt.Name, out var ct)    ? (Z42Type)ct
                               : _interfaces.TryGetValue(nt.Name, out var it) ? it
                               : new Z42PrimType(nt.Name),
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
