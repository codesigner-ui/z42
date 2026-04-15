using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

/// <summary>
/// Code generator: walks the AST and emits an IrModule.
///
/// Module-level state (class/function maps, string pool, overload info) lives here.
/// Per-function emission is delegated to <see cref="FunctionEmitter"/>, which is
/// created fresh for each function to naturally isolate function-level state.
/// </summary>
public sealed class IrGen
{
    internal readonly StdlibCallIndex _stdlibIndex;
    internal readonly LanguageFeatures _features;
    /// Semantic information produced by TypeChecker; null when running without type-check.
    internal readonly SemanticModel? _semanticModel;

    // Stdlib namespaces used by this compilation unit (populated during codegen).
    internal readonly HashSet<string> _usedStdlibNamespaces = new();

    /// The set of stdlib namespaces that were actually called during this compilation.
    /// Populated after Generate() returns; consumed by BuildCommand to record dependencies.
    public IReadOnlySet<string> UsedStdlibNamespaces => _usedStdlibNamespaces;

    internal readonly List<string> _strings = new();
    internal string? _namespace;
    internal Dictionary<string, HashSet<string>> _classMethods = new();
    internal Dictionary<string, HashSet<string>> _classStaticMethods = new();
    internal HashSet<(string, string)> _overloadedInstanceMethods = new();
    internal HashSet<(string, string)> _overloadedStaticMethods   = new();
    internal Dictionary<string, HashSet<string>> _classInstanceFields = new();
    internal Dictionary<string, Dictionary<string, Expr?>> _classStaticFieldInits = new();
    internal Dictionary<string, string> _classBaseNames = new();
    internal HashSet<string> _topLevelFunctionNames = new();
    internal Dictionary<string, IReadOnlyList<Param>> _funcParams = new();
    internal readonly Dictionary<string, long> _enumConstants = new();

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

        // Collect enum constants: "EnumName.Member" → i64 value
        foreach (var en in cu.Enums)
            foreach (var m in en.Members)
                _enumConstants[$"{en.Name}.{m.Name}"] = m.Value ?? 0;

        // Detect overloaded method names (same class, same static-ness, same name).
        foreach (var cls in cu.Classes)
        {
            var qualName = QualifyName(cls.Name);
            foreach (var grp in cls.Methods.Where(m => !m.IsStatic).GroupBy(m => m.Name).Where(g => g.Count() > 1))
                _overloadedInstanceMethods.Add((qualName, grp.Key));
            foreach (var grp in cls.Methods.Where(m =>  m.IsStatic).GroupBy(m => m.Name).Where(g => g.Count() > 1))
                _overloadedStaticMethods.Add((qualName, grp.Key));
        }

        // Build class method/field maps for call resolution (keyed by qualified class name).
        foreach (var cls in cu.Classes)
        {
            var qualName = QualifyName(cls.Name);
            _classMethods[qualName] = cls.Methods
                .Where(m => !m.IsStatic)
                .Select(m => _overloadedInstanceMethods.Contains((qualName, m.Name))
                    ? $"{m.Name}${m.Params.Count}" : m.Name)
                .ToHashSet();
            _classStaticMethods[qualName] = cls.Methods
                .Where(m =>  m.IsStatic)
                .Select(m => _overloadedStaticMethods.Contains((qualName, m.Name))
                    ? $"{m.Name}${m.Params.Count}" : m.Name)
                .ToHashSet();
            _classInstanceFields[qualName] = cls.Fields
                .Where(f => !f.IsStatic).Select(f => f.Name).ToHashSet();
            if (cls.BaseClass is not null)
                _classBaseNames[qualName] = QualifyName(cls.BaseClass);
        }

        // Collect static fields with their initializers
        foreach (var cls in cu.Classes)
        {
            var staticFields = cls.Fields.Where(f => f.IsStatic)
                .ToDictionary(f => f.Name, f => f.Initializer);
            if (staticFields.Count > 0)
                _classStaticFieldInits[QualifyName(cls.Name)] = staticFields;
        }

        // Collect top-level function names for qualified call resolution
        _topLevelFunctionNames = cu.Functions.Select(f => f.Name).ToHashSet();

        // Collect function param lists (including defaults) for call-site expansion.
        foreach (var fn in cu.Functions)
            _funcParams[QualifyName(fn.Name)] = fn.Params;
        foreach (var cls in cu.Classes)
        {
            var qualName = QualifyName(cls.Name);
            foreach (var m in cls.Methods)
            {
                bool isOverloaded = m.IsStatic
                    ? _overloadedStaticMethods.Contains((qualName, m.Name))
                    : _overloadedInstanceMethods.Contains((qualName, m.Name));
                string key = isOverloaded
                    ? $"{qualName}.{m.Name}${m.Params.Count}"
                    : $"{qualName}.{m.Name}";
                _funcParams[key] = m.Params;
            }
        }

        var classes   = cu.Classes.Select(EmitClassDesc).ToList();
        var functions = new List<IrFunction>();

        // If any static fields exist, prepend the __static_init__ function
        if (cu.Classes.Any(cls => cls.Fields.Any(f => f.IsStatic)))
            functions.Add(new FunctionEmitter(this).EmitStaticInit(cu));

        foreach (var cls in cu.Classes)
            functions.AddRange(cls.Methods.Where(m => !m.IsAbstract).Select(m => EmitMethod(cls.Name, m)));
        functions.AddRange(cu.Functions.Select(EmitFunction));
        return new IrModule(cu.Namespace ?? "main", _strings, classes, functions);
    }

    // ── Per-function delegation ──────────────────────────────────────────────

    private IrFunction EmitMethod(string className, FunctionDecl method)
    {
        var qualClass  = QualifyName(className);
        bool overloaded = method.IsStatic
            ? _overloadedStaticMethods.Contains((qualClass, method.Name))
            : _overloadedInstanceMethods.Contains((qualClass, method.Name));
        string methodIrName = overloaded
            ? $"{qualClass}.{method.Name}${method.Params.Count}"
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

        return new FunctionEmitter(this).EmitMethod(className, method, methodIrName);
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

        return new FunctionEmitter(this).EmitFunction(fn);
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
        var args = Enumerable.Range(0, totalParams).ToList();
        int dst  = totalParams;
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
        string qualName = QualifyName(className);
        if (_classStaticFieldInits.TryGetValue(qualName, out var fields)
            && fields.ContainsKey(fieldName))
            return $"{qualName}.{fieldName}";
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
