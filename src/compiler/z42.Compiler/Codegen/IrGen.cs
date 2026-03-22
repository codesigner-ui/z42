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
    // Top-level function names (unqualified) — used to qualify free function calls
    private HashSet<string> _topLevelFunctionNames = new();

    // Per-function state
    private int _nextReg;
    private int _nextLabelId;
    private Dictionary<string, int> _locals = new();  // parameter name → register
    private HashSet<string> _mutableVars = new();     // local variable names (use Load/Store)
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

        // Build class method maps for call resolution (keyed by qualified class name)
        foreach (var cls in cu.Classes)
        {
            _classMethods[QualifyName(cls.Name)]       = cls.Methods.Where(m => !m.IsStatic).Select(m => m.Name).ToHashSet();
            _classStaticMethods[QualifyName(cls.Name)] = cls.Methods.Where(m =>  m.IsStatic).Select(m => m.Name).ToHashSet();
        }

        // Collect top-level function names for qualified call resolution
        _topLevelFunctionNames = cu.Functions.Select(f => f.Name).ToHashSet();

        var classes   = cu.Classes.Select(EmitClassDesc).ToList();
        var functions = new List<IrFunction>();
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
        bool isStatic = method.IsStatic;
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
        _nextReg        = fn.Params.Count;
        _nextLabelId    = 0;
        _locals         = new Dictionary<string, int>();
        _mutableVars    = new HashSet<string>();
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
}
