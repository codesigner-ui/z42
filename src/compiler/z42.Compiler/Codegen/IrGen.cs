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

    // Per-function state
    private int _nextReg;
    private int _nextLabelId;
    private Dictionary<string, int> _locals = new();  // parameter name → register
    private HashSet<string> _mutableVars = new();     // local variable names (use Load/Store)
    private List<IrBlock> _blocks = new();
    // Loop context: (breakLabel, continueLabel) for the innermost enclosing loop
    private Stack<(string Break, string Continue)> _loopStack = new();

    // Current (open) block
    private string _curLabel      = "entry";
    private List<IrInstr> _curInstrs = new();
    private bool _blockEnded;

    // ── Public API ─────────────────────────────────────────────────────────────

    public IrModule Generate(CompilationUnit cu)
    {
        var functions = cu.Functions.Select(EmitFunction).ToList();
        return new IrModule(cu.Namespace ?? "main", _strings, functions);
    }

    // ── Function ────────────────────────────────────────────────────────────────

    private IrFunction EmitFunction(FunctionDecl fn)
    {
        _nextReg     = fn.Params.Count;
        _nextLabelId = 0;
        _locals      = new Dictionary<string, int>();
        _mutableVars = new HashSet<string>();
        _blocks      = new List<IrBlock>();
        _loopStack   = new Stack<(string, string)>();

        StartBlock("entry");

        for (int i = 0; i < fn.Params.Count; i++)
            _locals[fn.Params[i].Name] = i;  // parameters → direct registers

        EmitBlock(fn.Body);

        if (!_blockEnded)
            EndBlock(new RetTerm(null));

        var retType = fn.ReturnType is VoidType ? "void" : TypeName(fn.ReturnType);
        return new IrFunction(fn.Name, fn.Params.Count, retType, "Interp", _blocks);
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

    private static string TypeName(TypeExpr t) => t switch
    {
        NamedType nt  => nt.Name,
        VoidType      => "void",
        OptionType ot => TypeName(ot.Inner) + "?",
        ArrayType at  => TypeName(at.Element) + "[]",
        _             => "unknown"
    };
}
