using Z42.Compiler.Parser;
using Z42.IR;

namespace Z42.Compiler.Codegen;

/// <summary>
/// Code generator: walks the AST and emits an IrModule.
///
/// Convention: function parameters occupy registers 0..param_count-1.
/// Local variables are tracked by name → register in _locals.
///
/// Control flow uses multiple basic blocks with labeled branches.
/// StartBlock(label) begins a new open block.
/// EndBlock(term) seals the current block with a terminator.
/// _blockEnded prevents double-termination after return/break/continue.
/// _loopStack tracks (breakLabel, continueLabel) for the innermost loop.
///
/// Implementation is split across partial class files:
/// • IrGen.cs         — core state, public API, block management, helpers
/// • IrGenStmts.cs    — statement + control flow emission
/// • IrGenExprs.cs    — expression, call, and string interpolation emission
/// </summary>
public sealed partial class IrGen
{
    private readonly List<string> _strings = new();
    // Namespace for the current compilation unit (null = no namespace declared)
    private string? _namespace;
    // Class name → set of instance method names (for call resolution)
    private Dictionary<string, HashSet<string>> _classMethods = new();
    // Class name → set of static method names (for static call dispatch)
    private Dictionary<string, HashSet<string>> _classStaticMethods = new();
    // Class name → set of instance field names (qualified, for implicit this.field access)
    private Dictionary<string, HashSet<string>> _classInstanceFields = new();
    // Qualified class name → static field name → initializer expr (null = default)
    private Dictionary<string, Dictionary<string, Expr?>> _classStaticFieldInits = new();
    // Qualified class name → qualified base class name (for inherited field resolution)
    private Dictionary<string, string> _classBaseNames = new();
    // Top-level function names (unqualified) — used to qualify free function calls
    private HashSet<string> _topLevelFunctionNames = new();
    // Qualified function name → param list (for default-value expansion at call sites)
    private Dictionary<string, IReadOnlyList<Param>> _funcParams = new();
    // Class name of the method currently being emitted (null for top-level functions and __static_init__)
    private string? _currentClassName;

    // Per-function state
    private int _nextReg;
    private int _nextLabelId;
    private Dictionary<string, int> _locals = new();  // parameter name → register
    private HashSet<string> _mutableVars = new();     // local variable names (use Load/Store)
    // Instance field names for the current class method (enables implicit `this.field` access)
    private HashSet<string> _instanceFields = new();
    private List<IrBlock> _blocks = new();
    private List<IrExceptionEntry> _exceptionTable = new();
    // Loop context: (breakLabel, continueLabel) for the innermost enclosing loop
    private Stack<(string Break, string Continue)> _loopStack = new();

    // Current (open) block
    private string _curLabel      = "entry";
    private List<IrInstr> _curInstrs = new();
    private bool _blockEnded;

    // ── Public API ─────────────────────────────────────────────────────────────

    public IrModule Generate(CompilationUnit cu)
    {
        _namespace = cu.Namespace;

        // Collect enum constants: "EnumName.Member" → i64 value
        foreach (var en in cu.Enums)
            foreach (var m in en.Members)
                _enumConstants[$"{en.Name}.{m.Name}"] = m.Value ?? 0;

        // Build class method/field maps for call resolution (keyed by qualified class name)
        foreach (var cls in cu.Classes)
        {
            _classMethods[QualifyName(cls.Name)]        = cls.Methods.Where(m => !m.IsStatic).Select(m => m.Name).ToHashSet();
            _classStaticMethods[QualifyName(cls.Name)]  = cls.Methods.Where(m =>  m.IsStatic).Select(m => m.Name).ToHashSet();
            _classInstanceFields[QualifyName(cls.Name)] = cls.Fields.Where(f => !f.IsStatic).Select(f => f.Name).ToHashSet();
            if (cls.BaseClass is not null)
                _classBaseNames[QualifyName(cls.Name)] = QualifyName(cls.BaseClass);
        }

        // Collect static fields with their initializers
        foreach (var cls in cu.Classes)
        {
            var staticFields = cls.Fields.Where(f => f.IsStatic).ToDictionary(f => f.Name, f => f.Initializer);
            if (staticFields.Count > 0)
                _classStaticFieldInits[QualifyName(cls.Name)] = staticFields;
        }

        // Collect top-level function names for qualified call resolution
        _topLevelFunctionNames = cu.Functions.Select(f => f.Name).ToHashSet();

        // Collect function param lists (including defaults) for call-site expansion
        foreach (var fn in cu.Functions)
            _funcParams[QualifyName(fn.Name)] = fn.Params;
        foreach (var cls in cu.Classes)
            foreach (var m in cls.Methods)
                _funcParams[$"{QualifyName(cls.Name)}.{m.Name}"] = m.Params;

        var classes   = cu.Classes.Select(EmitClassDesc).ToList();
        var functions = new List<IrFunction>();
        // If any static fields exist, prepend the __static_init__ function
        var staticInit = EmitStaticInit(cu);
        if (staticInit != null) functions.Add(staticInit);
        foreach (var cls in cu.Classes)
            functions.AddRange(cls.Methods.Select(m => EmitMethod(cls.Name, m)));
        functions.AddRange(cu.Functions.Select(EmitFunction));
        return new IrModule(cu.Namespace ?? "main", _strings, classes, functions);
    }

    private readonly Dictionary<string, long> _enumConstants = new();

    private IrClassDesc EmitClassDesc(ClassDecl cls) =>
        new(QualifyName(cls.Name),
            cls.BaseClass is null ? null : QualifyName(cls.BaseClass),
            cls.Fields.Where(f => !f.IsStatic).Select(f => new IrFieldDesc(f.Name, TypeName(f.Type))).ToList());

    private IrFunction EmitMethod(string className, FunctionDecl method)
    {
        if (method.IsExtern && method.NativeIntrinsic != null)
            return EmitNativeStub(
                $"{QualifyName(className)}.{method.Name}",
                method.Params.Count + (method.IsStatic ? 0 : 1),
                method.IsStatic ? 0 : 1,
                method.NativeIntrinsic,
                method.ReturnType is VoidType);

        bool isStatic = method.IsStatic;
        _currentClassName = className;
        // Static methods: params start at 0; instance methods: `this` = reg 0, params start at 1
        int paramOffset = isStatic ? 0 : 1;
        _nextReg        = method.Params.Count + paramOffset;
        _nextLabelId    = 0;
        _locals         = isStatic
            ? new Dictionary<string, int>()
            : new Dictionary<string, int> { ["this"] = 0 };
        _mutableVars    = new HashSet<string>();
        _blocks         = new List<IrBlock>();
        _exceptionTable = new List<IrExceptionEntry>();
        _loopStack      = new Stack<(string, string)>();
        // Track instance field names so bare `fieldName` → `this.fieldName` in instance methods
        _instanceFields = isStatic ? [] :
            (_classMethods.ContainsKey(QualifyName(className))
                ? GetClassInstanceFieldNames(className)
                : []);

        StartBlock("entry");

        for (int i = 0; i < method.Params.Count; i++)
            _locals[method.Params[i].Name] = i + paramOffset;

        EmitBlock(method.Body);

        if (!_blockEnded)
            EndBlock(new RetTerm(null));

        bool isCtor = !isStatic && method.Name == className;
        var retType = isCtor ? "void" : TypeName(method.ReturnType);
        string qualifiedName = $"{QualifyName(className)}.{method.Name}";
        var excTable = _exceptionTable.Count > 0 ? _exceptionTable : null;
        int paramCount = method.Params.Count + paramOffset;
        return new IrFunction(qualifiedName, paramCount, retType, "Interp", _blocks, excTable);
    }

    // ── Function ────────────────────────────────────────────────────────────────

    private IrFunction EmitFunction(FunctionDecl fn)
    {
        if (fn.IsExtern && fn.NativeIntrinsic != null)
            return EmitNativeStub(
                QualifyName(fn.Name),
                fn.Params.Count,
                0,
                fn.NativeIntrinsic,
                fn.ReturnType is VoidType);

        _currentClassName = null;  // top-level functions have no owning class
        _nextReg        = fn.Params.Count;
        _nextLabelId    = 0;
        _locals         = new Dictionary<string, int>();
        _mutableVars    = new HashSet<string>();
        _instanceFields = [];   // top-level functions have no implicit this
        _blocks         = new List<IrBlock>();
        _exceptionTable = new List<IrExceptionEntry>();
        _loopStack      = new Stack<(string, string)>();

        StartBlock("entry");

        for (int i = 0; i < fn.Params.Count; i++)
            _locals[fn.Params[i].Name] = i;  // parameters → direct registers

        EmitBlock(fn.Body);

        if (!_blockEnded)
            EndBlock(new RetTerm(null));

        var retType = fn.ReturnType is VoidType ? "void" : TypeName(fn.ReturnType);
        var excTable = _exceptionTable.Count > 0 ? _exceptionTable : null;
        return new IrFunction(QualifyName(fn.Name), fn.Params.Count, retType, "Interp", _blocks, excTable);
    }

    /// Emits a single-block function that forwards all parameters to a VM builtin.
    /// For instance methods totalParams includes the implicit this (reg 0); all registers
    /// are forwarded so that the builtin always receives [this, arg0, arg1, ...].
    private static IrFunction EmitNativeStub(
        string qualifiedName, int totalParams, int paramOffset,
        string intrinsicName, bool isVoid)
    {
        var args = Enumerable.Range(0, totalParams).ToList();
        int dst  = totalParams; // first free register after params
        var instrs = new List<IrInstr> { new BuiltinInstr(dst, intrinsicName, args) };
        var term   = new RetTerm(isVoid ? null : dst);
        var block  = new IrBlock("entry", instrs, term);
        return new IrFunction(qualifiedName, totalParams, isVoid ? "void" : "object",
            "Interp", [block], null);
    }

    // ── Block management ────────────────────────────────────────────────────────

    private void StartBlock(string label)
    {
        _curLabel    = label;
        _curInstrs   = new List<IrInstr>();
        _blockEnded  = false;
    }

    private void EndBlock(IrTerminator term)
    {
        if (_blockEnded) return;
        _blocks.Add(new IrBlock(_curLabel, _curInstrs, term));
        _blockEnded = true;
    }

    private string FreshLabel(string hint) => $"{hint}_{_nextLabelId++}";

    private void Emit(IrInstr instr)
    {
        if (!_blockEnded)
            _curInstrs.Add(instr);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int Alloc() => _nextReg++;

    /// Returns instance field names for a class, including all inherited fields.
    private HashSet<string> GetClassInstanceFieldNames(string className)
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

    private int Intern(string s)
    {
        int idx = _strings.IndexOf(s);
        if (idx >= 0) return idx;
        _strings.Add(s);
        return _strings.Count - 1;
    }

    /// Finds the qualified class method name (e.g. "Foo.Bar") for a given method name,
    /// or returns the name as-is if it's a top-level function.
    internal string ResolveMethodName(string memberName)
    {
        foreach (var (className, methods) in _classMethods)
            if (methods.Contains(memberName))
                return $"{className}.{memberName}";
        return memberName;
    }

    /// Returns `{_namespace}.{name}` when a namespace is declared, otherwise `name`.
    private string QualifyName(string name) =>
        _namespace is null ? name : $"{_namespace}.{name}";

    private static string TypeName(TypeExpr t) => t switch
    {
        NamedType nt  => nt.Name,
        VoidType      => "void",
        OptionType ot => TypeName(ot.Inner) + "?",
        ArrayType at  => TypeName(at.Element) + "[]",
        _             => "unknown"
    };

    /// Emits a `__static_init__` function that initializes all static fields.
    /// Returns null if there are no static fields.
    private IrFunction? EmitStaticInit(CompilationUnit cu)
    {
        bool hasAny = cu.Classes.Any(cls => cls.Fields.Any(f => f.IsStatic));
        if (!hasAny) return null;

        _currentClassName = null;
        _nextReg        = 0;
        _nextLabelId    = 0;
        _locals         = new Dictionary<string, int>();
        _mutableVars    = new HashSet<string>();
        _instanceFields = [];
        _blocks         = new List<IrBlock>();
        _exceptionTable = [];
        _loopStack      = new Stack<(string, string)>();

        StartBlock("entry");

        foreach (var cls in cu.Classes)
        {
            foreach (var field in cls.Fields.Where(f => f.IsStatic))
            {
                string key = $"{QualifyName(cls.Name)}.{field.Name}";
                int valReg;
                if (field.Initializer != null)
                {
                    valReg = EmitExpr(field.Initializer);
                }
                else
                {
                    // Default value by type
                    valReg = Alloc();
                    IrInstr defaultInstr = field.Type switch
                    {
                        NamedType { Name: "int" or "long" or "short" or "byte" } => new ConstI64Instr(valReg, 0),
                        NamedType { Name: "double" or "float" }                   => new ConstF64Instr(valReg, 0.0),
                        NamedType { Name: "bool" }                                => new ConstBoolInstr(valReg, false),
                        _                                                          => new ConstNullInstr(valReg),
                    };
                    Emit(defaultInstr);
                }
                Emit(new StaticSetInstr(key, valReg));
            }
        }

        EndBlock(new RetTerm(null));
        string initName = QualifyName("__static_init__");
        return new IrFunction(initName, 0, "void", "Interp", _blocks);
    }

    /// Returns the qualified static field key "QualName.fieldName" if className is a class and fieldName is a static field.
    internal string? TryGetStaticFieldKey(string className, string fieldName)
    {
        string qualName = QualifyName(className);
        if (_classStaticFieldInits.TryGetValue(qualName, out var fields) && fields.ContainsKey(fieldName))
            return $"{qualName}.{fieldName}";
        return null;
    }
}
