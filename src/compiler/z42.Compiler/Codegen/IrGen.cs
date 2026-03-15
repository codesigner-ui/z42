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
/// _blockEnded prevents double-termination after return/break.
/// </summary>
public sealed class IrGen
{
    private readonly List<string> _strings = new();

    // Per-function state
    private int _nextReg;
    private int _nextLabelId;
    private Dictionary<string, int> _locals = new();  // parameter name → register
    private HashSet<string> _mutableVars = new();     // local variable names (use Load/Store)
    private List<IrBlock> _blocks = new();

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

    // ── Statements ────────────────────────────────────────────────────────────

    private void EmitBlock(BlockStmt block)
    {
        foreach (var stmt in block.Stmts)
        {
            if (_blockEnded) break;   // dead code after return
            EmitStmt(stmt);
        }
    }

    private void EmitStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case VarDeclStmt v:
                _mutableVars.Add(v.Name);   // mark as mutable local
                if (v.Init != null)
                {
                    int reg = EmitExpr(v.Init);
                    Emit(new StoreInstr(v.Name, reg));
                }
                break;

            case ReturnStmt r:
                EndBlock(r.Value != null
                    ? new RetTerm(EmitExpr(r.Value))
                    : new RetTerm(null));
                break;

            case ExprStmt e:
                EmitExpr(e.Expr);
                break;

            case BlockStmt b:
                EmitBlock(b);
                break;

            case IfStmt ifStmt:
                EmitIf(ifStmt);
                break;

            case WhileStmt ws:
                EmitWhile(ws);
                break;

            case ForStmt fs:
                EmitFor(fs);
                break;

            // foreach not yet lowered
            case ForeachStmt:
                break;
        }
    }

    // ── Control flow ──────────────────────────────────────────────────────────

    private void EmitIf(IfStmt ifStmt)
    {
        int condReg   = EmitExpr(ifStmt.Condition);
        string thenLbl = FreshLabel("then");
        string elseLbl = FreshLabel(ifStmt.Else != null ? "else" : "end");
        string endLbl  = ifStmt.Else != null ? FreshLabel("end") : elseLbl;

        EndBlock(new BrCondTerm(condReg, thenLbl, elseLbl));

        // then branch
        StartBlock(thenLbl);
        EmitBlock(ifStmt.Then);
        if (!_blockEnded) EndBlock(new BrTerm(endLbl));

        // else branch (optional)
        if (ifStmt.Else != null)
        {
            StartBlock(elseLbl);
            EmitStmt(ifStmt.Else);
            if (!_blockEnded) EndBlock(new BrTerm(endLbl));
        }

        // merge / continuation
        StartBlock(endLbl);
    }

    private void EmitWhile(WhileStmt ws)
    {
        string condLbl = FreshLabel("cond");
        string bodyLbl = FreshLabel("body");
        string endLbl  = FreshLabel("end");

        EndBlock(new BrTerm(condLbl));

        StartBlock(condLbl);
        int condReg = EmitExpr(ws.Condition);
        EndBlock(new BrCondTerm(condReg, bodyLbl, endLbl));

        StartBlock(bodyLbl);
        EmitBlock(ws.Body);
        if (!_blockEnded) EndBlock(new BrTerm(condLbl));

        StartBlock(endLbl);
    }

    private void EmitFor(ForStmt fs)
    {
        if (fs.Init != null) EmitStmt(fs.Init);

        string condLbl = FreshLabel("cond");
        string bodyLbl = FreshLabel("body");
        string endLbl  = FreshLabel("end");

        EndBlock(new BrTerm(condLbl));

        StartBlock(condLbl);
        if (fs.Condition != null)
        {
            int condReg = EmitExpr(fs.Condition);
            EndBlock(new BrCondTerm(condReg, bodyLbl, endLbl));
        }
        else
        {
            EndBlock(new BrTerm(bodyLbl));  // infinite loop
        }

        StartBlock(bodyLbl);
        EmitBlock(fs.Body);
        if (!_blockEnded)
        {
            if (fs.Increment != null) EmitExpr(fs.Increment);
            EndBlock(new BrTerm(condLbl));
        }

        StartBlock(endLbl);
    }

    // ── Expressions ──────────────────────────────────────────────────────────

    private int EmitExpr(Expr expr)
    {
        switch (expr)
        {
            case LitStrExpr s:
            {
                int dst = Alloc();
                Emit(new ConstStrInstr(dst, Intern(s.Value)));
                return dst;
            }
            case LitIntExpr n:
            {
                int dst = Alloc();
                Emit(new ConstI64Instr(dst, n.Value));
                return dst;
            }
            case LitFloatExpr f:
            {
                int dst = Alloc();
                Emit(new ConstF64Instr(dst, f.Value));
                return dst;
            }
            case LitBoolExpr b:
            {
                int dst = Alloc();
                Emit(new ConstBoolInstr(dst, b.Value));
                return dst;
            }
            case LitNullExpr:
            {
                int dst = Alloc();
                Emit(new ConstStrInstr(dst, Intern("null")));
                return dst;
            }
            case InterpolatedStrExpr interp:
                return EmitInterpolation(interp);

            case IdentExpr id:
            {
                // Parameters: direct register reference (read-only)
                if (_locals.TryGetValue(id.Name, out int reg))
                    return reg;
                // Mutable locals: emit a Load from named slot
                if (_mutableVars.Contains(id.Name))
                {
                    int dst = Alloc();
                    Emit(new LoadInstr(dst, id.Name));
                    return dst;
                }
                throw new InvalidOperationException($"undefined variable `{id.Name}`");
            }

            case AssignExpr assign:
                return EmitAssign(assign);

            case CallExpr call:
                return EmitCall(call);

            case BinaryExpr bin:
                return EmitBinary(bin);

            case UnaryExpr u:
                return EmitUnary(u);

            case PostfixExpr post:
                return EmitPostfix(post);

            case ConditionalExpr ternary:
                return EmitTernary(ternary);

            default:
                throw new NotSupportedException(
                    $"expression type {expr.GetType().Name} not yet supported in IrGen");
        }
    }

    // ── Assignment ───────────────────────────────────────────────────────────

    private int EmitAssign(AssignExpr assign)
    {
        int valReg = EmitExpr(assign.Value);
        if (assign.Target is IdentExpr id)
        {
            if (_mutableVars.Contains(id.Name))
                Emit(new StoreInstr(id.Name, valReg));
            else
                _locals[id.Name] = valReg;  // parameter re-binding (rare)
        }
        return valReg;
    }

    // ── Unary / postfix ──────────────────────────────────────────────────────

    private int EmitUnary(UnaryExpr u)
    {
        if (u.Op == "await") return EmitExpr(u.Operand);  // async stripped

        int src = EmitExpr(u.Operand);
        int dst = Alloc();
        Emit(u.Op switch
        {
            "!" => (IrInstr)new NotInstr(dst, src),
            "-" => new NegInstr(dst, src),
            _   => new NotInstr(dst, src)   // fallback
        });
        return dst;
    }

    private int EmitPostfix(PostfixExpr post)
    {
        if (post.Operand is IdentExpr id)
        {
            int oldReg = EmitExpr(post.Operand);  // reads current value via Load if mutable
            int one    = Alloc();
            int newReg = Alloc();
            Emit(new ConstI64Instr(one, 1));
            Emit(post.Op == "++" ? new AddInstr(newReg, oldReg, one)
                                 : (IrInstr)new SubInstr(newReg, oldReg, one));
            if (_mutableVars.Contains(id.Name))
                Emit(new StoreInstr(id.Name, newReg));
            else
                _locals[id.Name] = newReg;
            return oldReg;   // postfix returns original value
        }
        return EmitExpr(post.Operand);
    }

    private int EmitTernary(ConditionalExpr ternary)
    {
        // Evaluate inline — both branches always run.
        // Proper phi-based ternary requires more complex IR; for now use boolean trick.
        // cond ? then : else  is lowered to if/else writing to a fresh var.
        int condReg   = EmitExpr(ternary.Cond);
        string thenLbl = FreshLabel("tern_then");
        string elseLbl = FreshLabel("tern_else");
        string endLbl  = FreshLabel("tern_end");

        // Allocate the result register BEFORE splitting blocks
        int result = Alloc();

        EndBlock(new BrCondTerm(condReg, thenLbl, elseLbl));

        StartBlock(thenLbl);
        int thenReg = EmitExpr(ternary.Then);
        // Copy to result slot (LoadInstr/StoreInstr aren't used here;
        // instead re-use the result register via a direct assignment trick)
        Emit(new AddInstr(result, thenReg, thenReg));  // result = thenReg (add 0 trick)
        EndBlock(new BrTerm(endLbl));

        StartBlock(elseLbl);
        int elseReg = EmitExpr(ternary.Else);
        Emit(new AddInstr(result, elseReg, elseReg));
        EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }

    // ── Call ─────────────────────────────────────────────────────────────────

    private int EmitCall(CallExpr call)
    {
        // Console.WriteLine(...)  →  builtin __println
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: "Console" }, Member: "WriteLine" })
        {
            var argRegs  = call.Args.Select(EmitExpr).ToList();
            var printReg = argRegs.Count == 1 ? argRegs[0] : EmitConcat(argRegs);
            int dst = Alloc();
            Emit(new BuiltinInstr(dst, "__println", [printReg]));
            return dst;
        }

        // Console.Write(...)  →  builtin __print
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: "Console" }, Member: "Write" })
        {
            var argRegs  = call.Args.Select(EmitExpr).ToList();
            var printReg = argRegs.Count == 1 ? argRegs[0] : EmitConcat(argRegs);
            int dst = Alloc();
            Emit(new BuiltinInstr(dst, "__print", [printReg]));
            return dst;
        }

        // Direct function call: Greet(args)
        if (call.Callee is IdentExpr funcId)
        {
            var argRegs = call.Args.Select(EmitExpr).ToList();
            int dst = Alloc();
            Emit(new CallInstr(dst, funcId.Name, argRegs));
            return dst;
        }

        // Method call on an object: obj.Method(args) — best-effort
        if (call.Callee is MemberExpr m)
        {
            var argRegs = new List<int> { EmitExpr(m.Target) };
            argRegs.AddRange(call.Args.Select(EmitExpr));
            int dst = Alloc();
            Emit(new CallInstr(dst, m.Member, argRegs));
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
            ">"  => new GtInstr(dst, a, b),
            ">=" => new GeInstr(dst, a, b),
            "&&" => new AndInstr(dst, a, b),
            "||" => new OrInstr(dst, a, b),
            _    => throw new NotSupportedException($"operator `{bin.Op}` not yet supported")
        };
        Emit(instr);
        return dst;
    }

    // ── String interpolation ──────────────────────────────────────────────────

    private int EmitInterpolation(InterpolatedStrExpr interp)
    {
        if (interp.Parts.Count == 0)
        {
            int dst = Alloc();
            Emit(new ConstStrInstr(dst, Intern("")));
            return dst;
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
        Emit(new ConstStrInstr(dst, Intern(tp.Text)));
        return dst;
    }

    private int EmitExprPart(ExprPart ep)
    {
        int exprReg = EmitExpr(ep.Inner);

        // Conservatively wrap non-string expressions with to_str
        bool isStringLit = ep.Inner is LitStrExpr or InterpolatedStrExpr;
        if (!isStringLit)
        {
            int strReg = Alloc();
            Emit(new ToStrInstr(strReg, exprReg));
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
            Emit(new StrConcatInstr(dst, result, regs[i]));
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
