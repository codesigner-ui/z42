using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

/// <summary>
/// Code generator: emits an IrModule from a CompilationUnit + SemanticModel.
///
/// Module-level state (class/function maps, string pool) lives here; most of it
/// is now populated from <see cref="SemanticModel"/> rather than re-traversing
/// the AST.  Per-function emission is delegated to <see cref="FunctionEmitter"/>.
/// </summary>
public sealed class IrGen
{
    internal readonly StdlibCallIndex _stdlibIndex;
    internal readonly LanguageFeatures _features;
    internal readonly SemanticModel? _semanticModel;

    // Stdlib namespaces used by this compilation unit (populated during codegen).
    internal readonly HashSet<string> _usedStdlibNamespaces = new();

    /// The set of stdlib namespaces that were actually called during this compilation.
    public IReadOnlySet<string> UsedStdlibNamespaces => _usedStdlibNamespaces;

    internal readonly List<string> _strings = new();
    internal string? _namespace;

    // ── Derived from SemanticModel ───────────────────────────────────────────
    internal Dictionary<string, HashSet<string>> _classMethods = new();
    internal Dictionary<string, HashSet<string>> _classStaticMethods = new();
    internal Dictionary<string, HashSet<string>> _classInstanceFields = new();
    internal Dictionary<string, string> _classBaseNames = new();
    internal HashSet<string> _topLevelFunctionNames = new();
    internal readonly Dictionary<string, long> _enumConstants = new();
    // Param lists still come from AST (needed for FillDefaults BoundExpr lookup).
    internal Dictionary<string, IReadOnlyList<Param>> _funcParams = new();

    // ── Constructor ────────────────────────────────────────────────────────────

    public IrGen(StdlibCallIndex? stdlibIndex = null, LanguageFeatures? features = null,
                 SemanticModel? semanticModel = null)
    {
        _stdlibIndex    = stdlibIndex ?? StdlibCallIndex.Empty;
        _features       = features ?? LanguageFeatures.Phase1;
        _semanticModel  = semanticModel;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public IrModule Generate(CompilationUnit cu)
    {
        _namespace = cu.Namespace;

        if (_semanticModel is null)
            throw new InvalidOperationException(
                "SemanticModel is required for code generation.");

        // ── Populate from SemanticModel (no overload re-detection) ────────────

        foreach (var (key, val) in _semanticModel.EnumConstants)
            _enumConstants[key] = val;

        foreach (var (shortName, ct) in _semanticModel.Classes)
        {
            var qualName = QualifyName(shortName);
            _classMethods[qualName]       = ct.Methods.Keys.ToHashSet();
            _classStaticMethods[qualName] = ct.StaticMethods.Keys.ToHashSet();
            _classInstanceFields[qualName] = ct.Fields.Keys.ToHashSet();
            if (ct.BaseClassName is not null)
                _classBaseNames[qualName] = QualifyName(ct.BaseClassName);
        }

        _topLevelFunctionNames = _semanticModel.Funcs.Keys.ToHashSet();

        // ── Param lists from AST (for FillDefaults BoundExpr key lookup) ─────

        foreach (var fn in cu.Functions)
            _funcParams[QualifyName(fn.Name)] = fn.Params;
        foreach (var cls in cu.Classes)
        {
            var qualName = QualifyName(cls.Name);
            if (!_semanticModel.Classes.TryGetValue(cls.Name, out var ct))
                throw new InvalidOperationException(
                    $"IrGen: class `{cls.Name}` not found in SemanticModel (TypeChecker bug)");
            foreach (var m in cls.Methods)
            {
                string arityKey = $"{m.Name}${m.Params.Count}";
                bool isOverloaded = m.IsStatic
                    ? ct.StaticMethods.ContainsKey(arityKey)
                    : ct.Methods.ContainsKey(arityKey);
                string key = isOverloaded
                    ? $"{qualName}.{arityKey}"
                    : $"{qualName}.{m.Name}";
                _funcParams[key] = m.Params;
            }
        }

        // ── Emit ──────────────────────────────────────────────────────────────

        var classes   = cu.Classes.Select(EmitClassDesc).ToList();
        var functions = new List<IrFunction>();

        if (cu.Classes.Any(cls => cls.Fields.Any(f => f.IsStatic)))
            functions.Add(new FunctionEmitter(this).EmitStaticInit(cu));

        foreach (var cls in cu.Classes)
            functions.AddRange(cls.Methods.Where(m => !m.IsAbstract)
                .Select(m => EmitMethod(cls.Name, m)));
        functions.AddRange(cu.Functions.Select(EmitFunction));
        var module = new IrModule(cu.Namespace ?? "main", _strings, classes, functions);
        IrVerifier.VerifyOrThrow(module);
        return module;
    }

    // ── Per-function delegation ──────────────────────────────────────────────

    private IrFunction EmitMethod(string className, FunctionDecl method)
    {
        var qualClass = QualifyName(className);
        // Overload detection via SemanticModel method keys (already $N suffixed).
        string arityKey = $"{method.Name}${method.Params.Count}";
        bool overloaded = method.IsStatic
            ? _classStaticMethods.TryGetValue(qualClass, out var sSet) && sSet.Contains(arityKey)
            : _classMethods.TryGetValue(qualClass, out var mSet) && mSet.Contains(arityKey);
        string methodIrName = overloaded
            ? $"{qualClass}.{arityKey}"
            : $"{qualClass}.{method.Name}";

        if (method.IsExtern && method.NativeIntrinsic != null)
        {
            var stub = EmitNativeStub(
                methodIrName,
                method.Params.Count + (method.IsStatic ? 0 : 1),
                method.IsStatic ? 0 : 1,
                method.NativeIntrinsic,
                method.ReturnType is VoidType);
            return stub with { IsStatic = method.IsStatic };
        }

        var body = GetBoundBody(method);
        return new FunctionEmitter(this).EmitMethod(className, method, body, methodIrName);
    }

    private IrFunction EmitFunction(FunctionDecl fn)
    {
        if (fn.IsExtern && fn.NativeIntrinsic != null)
            return EmitNativeStub(
                QualifyName(fn.Name),
                fn.Params.Count,
                0,
                fn.NativeIntrinsic,
                fn.ReturnType is VoidType);

        var body = GetBoundBody(fn);
        return new FunctionEmitter(this).EmitFunction(fn, body);
    }

    private BoundBlock GetBoundBody(FunctionDecl fn)
    {
        if (!_semanticModel!.BoundBodies.TryGetValue(fn, out var body))
            throw new InvalidOperationException(
                $"No BoundBody found for `{fn.Name}`; was it excluded from type-checking?");
        return body;
    }

    // ── Class descriptors ────────────────────────────────────────────────────

    private IrClassDesc EmitClassDesc(ClassDecl cls)
    {
        var baseClass = cls.BaseClass is not null
            ? QualifyName(cls.BaseClass)
            : (cls.IsStruct || cls.IsRecord || WellKnownNames.IsObjectClass(cls.Name))
                ? null : "Std.Object";
        return new(QualifyName(cls.Name), baseClass,
            cls.Fields.Where(f => !f.IsStatic)
                .Select(f => new IrFieldDesc(f.Name, TypeName(f.Type))).ToList());
    }

    /// Emits a single-block function that forwards all parameters to a VM builtin.
    private static IrFunction EmitNativeStub(
        string qualifiedName, int totalParams, int paramOffset,
        string intrinsicName, bool isVoid)
    {
        var args = Enumerable.Range(0, totalParams)
            .Select(i => new TypedReg(i, IrType.Unknown)).ToList();
        var dst  = new TypedReg(totalParams, isVoid ? IrType.Void : IrType.Unknown);
        var instrs = new List<IrInstr> { new BuiltinInstr(dst, intrinsicName, args) };
        var term   = new RetTerm(isVoid ? null : dst);
        var block  = new IrBlock("entry", instrs, term);
        return new IrFunction(qualifiedName, totalParams, isVoid ? "void" : "object",
            "Interp", [block], null, MaxReg: totalParams + 1);
    }

    // ── Module-level helpers ─────────────────────────────────────────────────

    internal string QualifyName(string name) =>
        _namespace is null ? name : $"{_namespace}.{name}";

    internal int Intern(string s)
    {
        int idx = _strings.IndexOf(s);
        if (idx >= 0) return idx;
        _strings.Add(s);
        return _strings.Count - 1;
    }

    /// Returns instance field names for a class, including all inherited fields.
    internal HashSet<string> GetClassInstanceFieldNames(string className)
    {
        var result = new HashSet<string>();
        var current = QualifyName(className);
        while (current is not null)
        {
            if (_classInstanceFields.TryGetValue(current, out var fields))
                result.UnionWith(fields);
            _classBaseNames.TryGetValue(current, out current!);
        }
        return result;
    }

    /// Finds the _funcParams key for a virtual call default expansion.
    internal string? FindVcallParamsKey(string methodName, int suppliedArgCount)
    {
        foreach (var (cls, methods) in _classMethods)
        {
            if (!methods.Contains(methodName)) continue;
            string key = $"{cls}.{methodName}";
            if (_funcParams.TryGetValue(key, out var parms) && parms.Count > suppliedArgCount)
                return key;
        }
        return null;
    }

    /// Returns the qualified static field key if className has a static field named fieldName.
    internal string? TryGetStaticFieldKey(string className, string fieldName)
    {
        if (_semanticModel!.Classes.TryGetValue(className, out var ct)
            && ct.StaticFields.ContainsKey(fieldName))
            return $"{QualifyName(className)}.{fieldName}";
        return null;
    }

    private static string TypeName(TypeExpr t) => t switch
    {
        NamedType nt  => nt.Name,
        VoidType      => "void",
        OptionType ot => TypeName(ot.Inner) + "?",
        ArrayType at  => TypeName(at.Element) + "[]",
        _             => "unknown"
    };
}
