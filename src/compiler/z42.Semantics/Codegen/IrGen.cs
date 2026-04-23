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
public sealed class IrGen : IEmitterContext
{
    private readonly DependencyIndex _depIndex;
    internal readonly LanguageFeatures _features;
    private SemanticModel? _semanticModel;

    // Dependency namespaces used by this compilation unit (populated during codegen).
    private readonly HashSet<string> _usedDepNamespaces = new();

    /// The set of dependency namespaces that were actually called during this compilation.
    public IReadOnlySet<string> UsedDepNamespaces => _usedDepNamespaces;

    private readonly List<string> _strings = new();
    private readonly Dictionary<string, int> _stringIndex = new(StringComparer.Ordinal);
    private string? _namespace;

    // ── Derived from SemanticModel ───────────────────────────────────────────
    private readonly ClassRegistry _classRegistry = new();
    private HashSet<string> _topLevelFunctionNames = new();
    private readonly Dictionary<string, long> _enumConstants = new();
    // Param lists still come from AST (needed for FillDefaults BoundExpr lookup).
    private Dictionary<string, IReadOnlyList<Param>> _funcParams = new();

    // ── IEmitterContext explicit implementation ──────────────────────────────
    ClassRegistry IEmitterContext.ClassRegistry => _classRegistry;
    SemanticModel IEmitterContext.SemanticModel => _semanticModel!;
    HashSet<string> IEmitterContext.TopLevelFunctionNames => _topLevelFunctionNames;
    IReadOnlyDictionary<string, long> IEmitterContext.EnumConstants => _enumConstants;
    IReadOnlyDictionary<string, IReadOnlyList<Param>> IEmitterContext.FuncParams => _funcParams;
    DependencyIndex IEmitterContext.DepIndex => _depIndex;
    void IEmitterContext.TrackDepNamespace(string ns) => _usedDepNamespaces.Add(ns);
    string IEmitterContext.QualifyName(string name) => QualifyName(name);
    int IEmitterContext.Intern(string s) => Intern(s);
    HashSet<string> IEmitterContext.GetClassInstanceFieldNames(string className) =>
        GetClassInstanceFieldNames(className);
    string? IEmitterContext.FindVcallParamsKey(string methodName, int suppliedArgCount) =>
        FindVcallParamsKey(methodName, suppliedArgCount);
    string? IEmitterContext.TryGetStaticFieldKey(string className, string fieldName) =>
        TryGetStaticFieldKey(className, fieldName);
    IReadOnlyDictionary<string, string> IEmitterContext.ImportedClassNamespaces =>
        _semanticModel?.ImportedClassNamespaces ?? new Dictionary<string, string>();
    string IEmitterContext.QualifyClassName(string className)
    {
        // L3-G4d: local classes shadow imported ones. If a class exists in the semantic
        // model AND is not flagged as imported, use the current module namespace. Only
        // fall back to the imported namespace when the name truly refers to an import.
        var sem = _semanticModel;
        if (sem is not null
            && sem.Classes.ContainsKey(className)
            && !sem.ImportedClassNames.Contains(className))
            return QualifyName(className);
        return sem?.ImportedClassNamespaces.TryGetValue(className, out var ns) == true
            ? $"{ns}.{className}" : QualifyName(className);
    }

    // ── Constructor ────────────────────────────────────────────────────────────

    public IrGen(DependencyIndex? depIndex = null, LanguageFeatures? features = null,
                 SemanticModel? semanticModel = null)
    {
        _depIndex       = depIndex ?? DependencyIndex.Empty;
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
            var qualBase = ct.BaseClassName is not null ? QualifyName(ct.BaseClassName) : null;
            _classRegistry.Register(qualName, ct, qualBase);
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
            ? _classRegistry.TryGetStaticMethods(qualClass, out var sSet) && sSet.Contains(arityKey)
            : _classRegistry.TryGetMethods(qualClass, out var mSet) && mSet.Contains(arityKey);
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
                .Select(f => new IrFieldDesc(f.Name, TypeName(f.Type))).ToList(),
            cls.TypeParams?.ToList(),
            BuildConstraintList(cls.Name, cls.TypeParams, _semanticModel?.ClassConstraints));
    }

    /// (L3-G3a) Build a parallel list of IrConstraintBundle aligned with `typeParams`.
    /// Returns null when the decl has no type params; returns a list with one entry per type
    /// param (empty bundle for unconstrained ones) otherwise.
    internal static List<IrConstraintBundle>? BuildConstraintList(
        string declName,
        IReadOnlyList<string>? typeParams,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>>? map)
    {
        if (typeParams is null || typeParams.Count == 0) return null;
        if (map is null || !map.TryGetValue(declName, out var bundles))
        {
            // Emit explicit empty bundles so reader/VM get aligned slots.
            return typeParams.Select(_ => EmptyBundle()).ToList();
        }
        var result = new List<IrConstraintBundle>(typeParams.Count);
        foreach (var tp in typeParams)
        {
            if (bundles.TryGetValue(tp, out var b))
                result.Add(new IrConstraintBundle(
                    b.RequiresClass, b.RequiresStruct,
                    b.BaseClass?.Name, b.Interfaces.Select(i => i.Name).ToList(),
                    b.TypeParamConstraint,
                    b.RequiresConstructor,
                    b.RequiresEnum));
            else
                result.Add(EmptyBundle());
        }
        return result;

        static IrConstraintBundle EmptyBundle() => new(false, false, null, new List<string>());
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
        if (_stringIndex.TryGetValue(s, out int idx)) return idx;
        idx = _strings.Count;
        _strings.Add(s);
        _stringIndex[s] = idx;
        return idx;
    }

    /// Returns instance field names for a class, including all inherited fields.
    internal HashSet<string> GetClassInstanceFieldNames(string className) =>
        _classRegistry.GetAllInstanceFields(QualifyName(className));

    /// Finds the _funcParams key for a virtual call default expansion.
    internal string? FindVcallParamsKey(string methodName, int suppliedArgCount) =>
        _classRegistry.FindVcallParamsKey(methodName, suppliedArgCount, _funcParams);

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
