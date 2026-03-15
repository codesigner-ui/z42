using Z42.Compiler.Parser;
using Z42.IR;

namespace Z42.Compiler.Codegen;

/// <summary>
/// Code generator: walks the AST and emits an IrModule.
/// Convention: function parameters occupy registers 0..param_count-1.
/// Local variables are assigned fresh registers starting from param_count.
/// </summary>
public sealed class IrGen
{
    private readonly List<string> _strings = new();
    private int _nextReg;
    private Dictionary<string, int> _locals = new();
    private List<IrInstr> _instrs = new();
    private IrTerminator? _term;

    // ── Public API ─────────────────────────────────────────────────────────────

    public IrModule Generate(CompilationUnit cu)
    {
        var functions = cu.Functions.Select(EmitFunction).ToList();
        return new IrModule(cu.Namespace ?? "main", _strings, functions);
    }

    // ── Function ────────────────────────────────────────────────────────────────

    private IrFunction EmitFunction(FunctionDecl fn)
    {
        _nextReg = fn.Params.Count;
        _locals  = new Dictionary<string, int>();
        _instrs  = new List<IrInstr>();
        _term    = null;

        // Parameters occupy registers 0..N-1
        for (int i = 0; i < fn.Params.Count; i++)
            _locals[fn.Params[i].Name] = i;

        EmitBlock(fn.Body);

        // Implicit void return if no explicit terminator
        _term ??= new RetTerm(null);

        var block = new IrBlock("entry", _instrs, _term);
        var retType = fn.ReturnType is VoidType ? "void" : TypeName(fn.ReturnType);
        return new IrFunction(fn.Name, fn.Params.Count, retType, "Interp", [block]);
    }

    private void EmitBlock(BlockStmt block)
    {
        foreach (var stmt in block.Stmts)
            EmitStmt(stmt);
    }

    // ── Statements ────────────────────────────────────────────────────────────

    private void EmitStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case VarDeclStmt v:
            {
                if (v.Init != null)
                {
                    int reg = EmitExpr(v.Init);
                    _locals[v.Name] = reg;
                }
                break;
            }
            case ReturnStmt r:
            {
                if (r.Value != null)
                {
                    int reg = EmitExpr(r.Value);
                    _term = new RetTerm(reg);
                }
                else
                {
                    _term = new RetTerm(null);
                }
                break;
            }
            case ExprStmt e:
                EmitExpr(e.Expr);
                break;

            case BlockStmt b:
                EmitBlock(b);
                break;

            default:
                // Other statements (if/while/for) — emit as nop for now
                break;
        }
    }

    // ── Expressions ──────────────────────────────────────────────────────────

    private int EmitExpr(Expr expr)
    {
        switch (expr)
        {
            case LitStrExpr s:
            {
                int idx = Intern(s.Value);
                int dst = Alloc();
                _instrs.Add(new ConstStrInstr(dst, idx));
                return dst;
            }
            case LitIntExpr n:
            {
                int dst = Alloc();
                _instrs.Add(new ConstI64Instr(dst, n.Value));
                return dst;
            }
            case LitFloatExpr f:
            {
                int dst = Alloc();
                _instrs.Add(new ConstF64Instr(dst, f.Value));
                return dst;
            }
            case LitBoolExpr b:
            {
                int dst = Alloc();
                _instrs.Add(new ConstBoolInstr(dst, b.Value));
                return dst;
            }
            case LitNullExpr:
            {
                int dst = Alloc();
                int idx = Intern("null");
                _instrs.Add(new ConstStrInstr(dst, idx)); // represent null as "null" string for now
                return dst;
            }
            case InterpolatedStrExpr interp:
                return EmitInterpolation(interp);

            case IdentExpr id:
            {
                if (_locals.TryGetValue(id.Name, out int reg))
                    return reg;
                throw new InvalidOperationException($"undefined variable `{id.Name}`");
            }

            case CallExpr call:
                return EmitCall(call);

            case BinaryExpr bin:
                return EmitBinary(bin);

            case UnaryExpr u when u.Op == "await":
                return EmitExpr(u.Operand); // async stripped for now

            default:
                throw new NotSupportedException($"expression type {expr.GetType().Name} not yet supported in IrGen");
        }
    }

    // ── Call ─────────────────────────────────────────────────────────────────

    private int EmitCall(CallExpr call)
    {
        // Console.WriteLine(...)  →  builtin __println
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: "Console" }, Member: "WriteLine" })
        {
            var argRegs = call.Args.Select(EmitExpr).ToList();
            // If multiple args, concat them with " " separator (best-effort)
            var printReg = argRegs.Count == 1 ? argRegs[0] : EmitConcat(argRegs);
            int dst = Alloc();
            _instrs.Add(new BuiltinInstr(dst, "__println", [printReg]));
            return dst;
        }

        // Console.Write(...)  →  builtin __print
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: "Console" }, Member: "Write" })
        {
            var argRegs = call.Args.Select(EmitExpr).ToList();
            var printReg = argRegs.Count == 1 ? argRegs[0] : EmitConcat(argRegs);
            int dst = Alloc();
            _instrs.Add(new BuiltinInstr(dst, "__print", [printReg]));
            return dst;
        }

        // Direct function call: Greet(args)
        if (call.Callee is IdentExpr funcId)
        {
            var argRegs = call.Args.Select(EmitExpr).ToList();
            int dst = Alloc();
            _instrs.Add(new CallInstr(dst, funcId.Name, argRegs));
            return dst;
        }

        // Method call on an object: obj.Method(args) — best-effort
        if (call.Callee is MemberExpr m)
        {
            var argRegs = new List<int> { EmitExpr(m.Target) };
            argRegs.AddRange(call.Args.Select(EmitExpr));
            int dst = Alloc();
            _instrs.Add(new CallInstr(dst, m.Member, argRegs));
            return dst;
        }

        throw new NotSupportedException($"call pattern not supported: {call.Callee.GetType().Name}");
    }

    // ── Binary ────────────────────────────────────────────────────────────────

    private int EmitBinary(BinaryExpr bin)
    {
        int a   = EmitExpr(bin.Left);
        int b   = EmitExpr(bin.Right);
        int dst = Alloc();
        IrInstr instr = bin.Op switch
        {
            "+"  => new AddInstr(dst, a, b),
            "-"  => new SubInstr(dst, a, b),
            "*"  => new MulInstr(dst, a, b),
            "/"  => new DivInstr(dst, a, b),
            "%"  => new RemInstr(dst, a, b),
            "==" => new EqInstr(dst, a, b),
            "!=" => new NeInstr(dst, a, b),
            "<"  => new LtInstr(dst, a, b),
            "<=" => new LeInstr(dst, a, b),
            _    => throw new NotSupportedException($"operator `{bin.Op}` not yet supported")
        };
        _instrs.Add(instr);
        return dst;
    }

    // ── String interpolation ──────────────────────────────────────────────────

    private int EmitInterpolation(InterpolatedStrExpr interp)
    {
        if (interp.Parts.Count == 0)
        {
            int emptyDst = Alloc();
            _instrs.Add(new ConstStrInstr(emptyDst, Intern("")));
            return emptyDst;
        }

        var partRegs = interp.Parts.Select(EmitPart).ToList();
        return EmitConcat(partRegs);
    }

    private int EmitPart(InterpolationPart part) => part switch
    {
        TextPart tp => EmitTextPart(tp),
        ExprPart ep => EmitExprPart(ep),
        _           => throw new NotSupportedException(part.GetType().Name)
    };

    private int EmitTextPart(TextPart tp)
    {
        int dst = Alloc();
        _instrs.Add(new ConstStrInstr(dst, Intern(tp.Text)));
        return dst;
    }

    private int EmitExprPart(ExprPart ep)
    {
        int exprReg = EmitExpr(ep.Inner);

        // Numeric literals need ToStr; string expressions are used as-is
        bool needsToStr = ep.Inner is LitIntExpr or LitFloatExpr or LitBoolExpr;

        if (needsToStr)
        {
            int strReg = Alloc();
            _instrs.Add(new ToStrInstr(strReg, exprReg));
            return strReg;
        }
        return exprReg;
    }

    private int EmitConcat(List<int> regs)
    {
        int result = regs[0];
        for (int i = 1; i < regs.Count; i++)
        {
            int dst = Alloc();
            _instrs.Add(new StrConcatInstr(dst, result, regs[i]));
            result = dst;
        }
        return result;
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
