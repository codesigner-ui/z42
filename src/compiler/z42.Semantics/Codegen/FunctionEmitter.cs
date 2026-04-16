using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

/// <summary>
/// Emits IR for a single function/method body.
/// Created fresh per function to naturally isolate function-level state,
/// eliminating the risk of cross-function state pollution.
///
/// Entry points accept a pre-bound <see cref="BoundBlock"/> produced by
/// the TypeChecker, so no ExprTypes lookup or _classInstanceVars heuristic
/// is needed during emission.
/// </summary>
internal sealed partial class FunctionEmitter
{
    private readonly IrGen _gen;

    // Per-function state — initialized by entry point methods, never carried across functions.
    private int _nextReg;
    private int _nextLabelId;
    private Dictionary<string, TypedReg> _locals = new();
    private HashSet<string> _mutableVars = new();
    private HashSet<string> _instanceFields = new();
    private List<IrBlock> _blocks = new();
    private List<IrExceptionEntry> _exceptionTable = new();
    private Stack<(string Break, string Continue)> _loopStack = new();
    private string _curLabel = "entry";
    private List<IrInstr> _curInstrs = new();
    private bool _blockEnded;
    private string? _currentClassName;

    internal FunctionEmitter(IrGen gen) => _gen = gen;

    // ── Entry points ─────────────────────────────────────────────────────────

    internal IrFunction EmitMethod(
        string className, FunctionDecl method, BoundBlock body, string methodIrName)
    {
        bool isStatic = method.IsStatic;
        _currentClassName = className;
        int paramOffset = isStatic ? 0 : 1;
        _nextReg = method.Params.Count + paramOffset;
        if (!isStatic) _locals["this"] = new TypedReg(0, IrType.Ref);
        _instanceFields = isStatic ? [] : _gen.GetClassInstanceFieldNames(className);

        StartBlock("entry");
        for (int i = 0; i < method.Params.Count; i++)
            _locals[method.Params[i].Name] = new TypedReg(i + paramOffset, ToIrType(method.Params[i].Type));

        // Emit base constructor call at the start of derived constructors
        bool isCtor = !isStatic && method.Name == className;
        if (isCtor && method.BaseCtorArgs is { }
            && _gen._classBaseNames.TryGetValue(_gen.QualifyName(className), out var baseQual))
        {
            var baseSimpleName = baseQual.Contains('.')
                ? baseQual[(baseQual.LastIndexOf('.') + 1)..] : baseQual;
            var baseCtorIrName = $"{baseQual}.{baseSimpleName}";
            var argRegs = new List<TypedReg> { new(0, IrType.Ref) };
            argRegs.AddRange(_gen._semanticModel!.BoundBaseCtorArgs[method].Select(EmitExpr));
            var dst = Alloc(IrType.Ref);
            Emit(new CallInstr(dst, baseCtorIrName, argRegs));
        }

        EmitBoundBlock(body);
        if (!_blockEnded) EndBlock(new RetTerm(null));

        var retType = isCtor ? "void" : TypeName(method.ReturnType);
        var excTable = _exceptionTable.Count > 0 ? _exceptionTable : null;
        int paramCount = method.Params.Count + paramOffset;
        return new IrFunction(methodIrName, paramCount, retType, "Interp", _blocks, excTable,
            IsStatic: isStatic, MaxReg: _nextReg);
    }

    internal IrFunction EmitFunction(FunctionDecl fn, BoundBlock body)
    {
        _currentClassName = null;
        _nextReg = fn.Params.Count;

        StartBlock("entry");
        for (int i = 0; i < fn.Params.Count; i++)
            _locals[fn.Params[i].Name] = new TypedReg(i, ToIrType(fn.Params[i].Type));

        EmitBoundBlock(body);
        if (!_blockEnded) EndBlock(new RetTerm(null));

        var retType = fn.ReturnType is VoidType ? "void" : TypeName(fn.ReturnType);
        var excTable = _exceptionTable.Count > 0 ? _exceptionTable : null;
        return new IrFunction(_gen.QualifyName(fn.Name), fn.Params.Count, retType,
            "Interp", _blocks, excTable, MaxReg: _nextReg);
    }

    internal IrFunction EmitStaticInit(CompilationUnit cu)
    {
        _currentClassName = null;
        StartBlock("entry");

        foreach (var cls in cu.Classes)
        {
            foreach (var field in cls.Fields.Where(f => f.IsStatic))
            {
                string key = $"{_gen.QualifyName(cls.Name)}.{field.Name}";
                TypedReg valReg;
                if (field.Initializer != null)
                {
                    valReg = EmitExpr(_gen._semanticModel!.BoundStaticInits[field]);
                }
                else
                {
                    valReg = field.Type switch
                    {
                        NamedType { Name: "int" } => Alloc(IrType.I32),
                        NamedType { Name: "long" } => Alloc(IrType.I64),
                        NamedType { Name: "short" } => Alloc(IrType.I16),
                        NamedType { Name: "byte" } => Alloc(IrType.U8),
                        NamedType { Name: "double" } => Alloc(IrType.F64),
                        NamedType { Name: "float" } => Alloc(IrType.F32),
                        NamedType { Name: "bool" } => Alloc(IrType.Bool),
                        _ => Alloc(IrType.Ref),
                    };
                    IrInstr defaultInstr = field.Type switch
                    {
                        NamedType { Name: "int" or "long" or "short" or "byte" }
                            => new ConstI64Instr(valReg, 0),
                        NamedType { Name: "double" or "float" }
                            => new ConstF64Instr(valReg, 0.0),
                        NamedType { Name: "bool" }
                            => new ConstBoolInstr(valReg, false),
                        _ => new ConstNullInstr(valReg),
                    };
                    Emit(defaultInstr);
                }
                Emit(new StaticSetInstr(key, valReg));
            }
        }

        EndBlock(new RetTerm(null));
        string initName = _gen.QualifyName("__static_init__");
        return new IrFunction(initName, 0, "void", "Interp", _blocks, MaxReg: _nextReg);
    }

    // ── Block management ─────────────────────────────────────────────────────

    private void StartBlock(string label)
    {
        _curLabel   = label;
        _curInstrs  = new List<IrInstr>();
        _blockEnded = false;
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

    private TypedReg Alloc(IrType type = IrType.Unknown) => new(_nextReg++, type);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string TypeName(TypeExpr t) => t switch
    {
        NamedType nt  => nt.Name,
        VoidType      => "void",
        OptionType ot => TypeName(ot.Inner) + "?",
        ArrayType at  => TypeName(at.Element) + "[]",
        _             => "unknown"
    };

    /// Write a new value back to a named variable.
    private void WriteBackName(string name, TypedReg valReg)
    {
        if (_mutableVars.Contains(name))
            Emit(new StoreInstr(name, valReg));
        else if (_instanceFields.Contains(name))
            Emit(new FieldSetInstr(new TypedReg(0, IrType.Ref), name, valReg));
        else
            _locals[name] = valReg;
    }

    // ── Z42Type / TypeExpr → IrType mapping ─────────────────────────────────

    /// Name → IrType lookup table (shared by both overloads, single source of truth).
    private static readonly Dictionary<string, IrType> IrTypeByName = new()
    {
        ["int"]    = IrType.I32,  ["i32"]    = IrType.I32,
        ["long"]   = IrType.I64,  ["i64"]    = IrType.I64,
        ["float"]  = IrType.F32,  ["f32"]    = IrType.F32,
        ["double"] = IrType.F64,  ["f64"]    = IrType.F64,
        ["bool"]   = IrType.Bool,
        ["char"]   = IrType.Char,
        ["string"] = IrType.Str,
        ["object"] = IrType.Ref,
        ["i8"]     = IrType.I8,   ["i16"]    = IrType.I16,
        ["u8"]     = IrType.U8,   ["u16"]    = IrType.U16,
        ["u32"]    = IrType.U32,  ["u64"]    = IrType.U64,
        ["sbyte"]  = IrType.I8,   ["short"]  = IrType.I16,
        ["byte"]   = IrType.U8,   ["ushort"] = IrType.U16,
        ["uint"]   = IrType.U32,  ["ulong"]  = IrType.U64,
    };

    /// Maps a Z42 semantic type to an IR type tag.
    internal static IrType ToIrType(Z42Type type) => type switch
    {
        Z42PrimType { Name: var n } when IrTypeByName.TryGetValue(n, out var ir) => ir,
        Z42ArrayType or Z42ClassType or Z42OptionType or Z42NullType => IrType.Ref,
        Z42VoidType => IrType.Void,
        _ => IrType.Unknown,
    };

    /// Maps a parser TypeExpr to an IrType (used for parameters where no Z42Type is available).
    internal static IrType ToIrType(TypeExpr typeExpr) => typeExpr switch
    {
        NamedType { Name: var n } when IrTypeByName.TryGetValue(n, out var ir) => ir,
        ArrayType or OptionType => IrType.Ref,
        VoidType => IrType.Void,
        _ => IrType.Unknown,
    };
}
