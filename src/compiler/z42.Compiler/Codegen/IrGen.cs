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

    // ── Statements ────────────────────────────────────────────────────────────

    private void EmitBlock(BlockStmt block)
    {
        foreach (var stmt in block.Stmts)
        {
            if (_blockEnded) break;   // dead code after return/break/continue
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

            case ForeachStmt fe:
                EmitForeach(fe);
                break;

            case BreakStmt:
                if (_loopStack.Count > 0)
                    EndBlock(new BrTerm(_loopStack.Peek().Break));
                break;

            case ContinueStmt:
                if (_loopStack.Count > 0)
                    EndBlock(new BrTerm(_loopStack.Peek().Continue));
                break;
        }
    }

    // ── Control flow ──────────────────────────────────────────────────────────

    private void EmitIf(IfStmt ifStmt)
    {
        int condReg    = EmitExpr(ifStmt.Condition);
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

        // continue → re-check condition; break → exit
        _loopStack.Push((endLbl, condLbl));
        StartBlock(bodyLbl);
        EmitBlock(ws.Body);
        if (!_blockEnded) EndBlock(new BrTerm(condLbl));
        _loopStack.Pop();

        StartBlock(endLbl);
    }

    private void EmitFor(ForStmt fs)
    {
        if (fs.Init != null) EmitStmt(fs.Init);

        string condLbl = FreshLabel("cond");
        string bodyLbl = FreshLabel("body");
        string incrLbl = FreshLabel("incr");
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

        // continue → increment; break → exit
        _loopStack.Push((endLbl, incrLbl));
        StartBlock(bodyLbl);
        EmitBlock(fs.Body);
        if (!_blockEnded) EndBlock(new BrTerm(incrLbl));
        _loopStack.Pop();

        StartBlock(incrLbl);
        if (fs.Increment != null) EmitExpr(fs.Increment);
        EndBlock(new BrTerm(condLbl));

        StartBlock(endLbl);
    }

    private void EmitForeach(ForeachStmt fe)
    {
        // Evaluate collection once in the current (pre-loop) block
        int arrReg = EmitExpr(fe.Collection);

        int lenReg = Alloc();
        Emit(new ArrayLenInstr(lenReg, arrReg));

        // Init counter variable
        string indexVar = $"__fe_i_{_nextLabelId}";
        _mutableVars.Add(indexVar);
        int zeroReg = Alloc();
        Emit(new ConstI32Instr(zeroReg, 0));
        Emit(new StoreInstr(indexVar, zeroReg));

        string condLbl = FreshLabel("fe_cond");
        string bodyLbl = FreshLabel("fe_body");
        string incrLbl = FreshLabel("fe_inc");
        string endLbl  = FreshLabel("fe_end");

        EndBlock(new BrTerm(condLbl));

        // Condition: i < len
        StartBlock(condLbl);
        int iReg   = Alloc();
        Emit(new LoadInstr(iReg, indexVar));
        int cmpReg = Alloc();
        Emit(new LtInstr(cmpReg, iReg, lenReg));
        EndBlock(new BrCondTerm(cmpReg, bodyLbl, endLbl));

        // Body: load element, bind loop variable, run body
        // continue → increment block; break → end
        _loopStack.Push((endLbl, incrLbl));
        StartBlock(bodyLbl);
        int iReg2   = Alloc();
        Emit(new LoadInstr(iReg2, indexVar));
        int elemReg = Alloc();
        Emit(new ArrayGetInstr(elemReg, arrReg, iReg2));
        _mutableVars.Add(fe.VarName);
        Emit(new StoreInstr(fe.VarName, elemReg));
        EmitBlock(fe.Body);
        if (!_blockEnded) EndBlock(new BrTerm(incrLbl));
        _loopStack.Pop();

        // Increment: i = i + 1
        StartBlock(incrLbl);
        int iReg3   = Alloc();
        Emit(new LoadInstr(iReg3, indexVar));
        int oneReg  = Alloc();
        Emit(new ConstI32Instr(oneReg, 1));
        int nextReg = Alloc();
        Emit(new AddInstr(nextReg, iReg3, oneReg));
        Emit(new StoreInstr(indexVar, nextReg));
        EndBlock(new BrTerm(condLbl));

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
                Emit(new ConstNullInstr(dst));
                return dst;
            }
            case InterpolatedStrExpr interp:
                return EmitInterpolation(interp);

            case LitCharExpr c:
            {
                int dst = Alloc();
                Emit(new ConstI32Instr(dst, (int)c.Value));
                return dst;
            }
            case IdentExpr id:
            {
                if (_locals.TryGetValue(id.Name, out int reg))
                    return reg;
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

            case CastExpr cast:
                return EmitExpr(cast.Operand);

            case MemberExpr m when m.Member == "Length":
            {
                int targetReg = EmitExpr(m.Target);
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__len", [targetReg]));
                return dst;
            }

            case MemberExpr m:
            {
                EmitExpr(m.Target);
                int dst = Alloc();
                Emit(new ConstNullInstr(dst));
                return dst;
            }

            case IndexExpr ix:
            {
                int targetReg = EmitExpr(ix.Target);
                int idxReg    = EmitExpr(ix.Index);
                int dst       = Alloc();
                Emit(new ArrayGetInstr(dst, targetReg, idxReg));
                return dst;
            }

            case ArrayCreateExpr ac:
            {
                int sizeReg = EmitExpr(ac.Size);
                int dst = Alloc();
                Emit(new ArrayNewInstr(dst, sizeReg));
                return dst;
            }

            case ArrayLitExpr al:
            {
                var elemRegs = al.Elements.Select(EmitExpr).ToList();
                int dst = Alloc();
                Emit(new ArrayNewLitInstr(dst, elemRegs));
                return dst;
            }

            case NewExpr newExpr:
            {
                var argRegs = newExpr.Args.Select(EmitExpr).ToList();
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__new_obj", argRegs));
                return dst;
            }

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
                _locals[id.Name] = valReg;
        }
        else if (assign.Target is IndexExpr ix)
        {
            int arrReg = EmitExpr(ix.Target);
            int idxReg = EmitExpr(ix.Index);
            Emit(new ArraySetInstr(arrReg, idxReg, valReg));
        }

        return valReg;
    }

    // ── Unary / postfix ──────────────────────────────────────────────────────

    private int EmitUnary(UnaryExpr u)
    {
        if (u.Op == "await") return EmitExpr(u.Operand);

        int src = EmitExpr(u.Operand);
        int dst = Alloc();
        Emit(u.Op switch
        {
            "!" => (IrInstr)new NotInstr(dst, src),
            "-" => new NegInstr(dst, src),
            _   => new NotInstr(dst, src)
        });
        return dst;
    }

    private int EmitPostfix(PostfixExpr post)
    {
        if (post.Operand is IdentExpr id)
        {
            int oldReg = EmitExpr(post.Operand);
            int one    = Alloc();
            int newReg = Alloc();
            Emit(new ConstI64Instr(one, 1));
            Emit(post.Op == "++" ? new AddInstr(newReg, oldReg, one)
                                 : (IrInstr)new SubInstr(newReg, oldReg, one));
            if (_mutableVars.Contains(id.Name))
                Emit(new StoreInstr(id.Name, newReg));
            else
                _locals[id.Name] = newReg;
            return oldReg;
        }
        return EmitExpr(post.Operand);
    }

    private int EmitTernary(ConditionalExpr ternary)
    {
        int condReg    = EmitExpr(ternary.Cond);
        string thenLbl = FreshLabel("tern_then");
        string elseLbl = FreshLabel("tern_else");
        string endLbl  = FreshLabel("tern_end");

        int result = Alloc();

        EndBlock(new BrCondTerm(condReg, thenLbl, elseLbl));

        StartBlock(thenLbl);
        int thenReg = EmitExpr(ternary.Then);
        Emit(new CopyInstr(result, thenReg));
        EndBlock(new BrTerm(endLbl));

        StartBlock(elseLbl);
        int elseReg = EmitExpr(ternary.Else);
        Emit(new CopyInstr(result, elseReg));
        EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }

    // ── Call ─────────────────────────────────────────────────────────────────

    private int EmitCall(CallExpr call)
    {
        // Console.WriteLine  →  builtin __println
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: "Console" }, Member: "WriteLine" })
        {
            var argRegs  = call.Args.Select(EmitExpr).ToList();
            var printReg = argRegs.Count == 1 ? argRegs[0] : EmitConcat(argRegs);
            int dst = Alloc();
            Emit(new BuiltinInstr(dst, "__println", [printReg]));
            return dst;
        }

        // Console.Write  →  builtin __print
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: "Console" }, Member: "Write" })
        {
            var argRegs  = call.Args.Select(EmitExpr).ToList();
            var printReg = argRegs.Count == 1 ? argRegs[0] : EmitConcat(argRegs);
            int dst = Alloc();
            Emit(new BuiltinInstr(dst, "__print", [printReg]));
            return dst;
        }

        // String built-in methods: s.Substring(), s.Contains(), etc.
        if (call.Callee is MemberExpr mMethod)
        {
            string? builtinName = mMethod.Member switch
            {
                "Substring"  => "__str_substring",
                "Contains"   => "__str_contains",
                "StartsWith" => "__str_starts_with",
                "EndsWith"   => "__str_ends_with",
                _            => null
            };

            if (builtinName != null)
            {
                int targetReg = EmitExpr(mMethod.Target);
                var argRegs   = new List<int> { targetReg };
                argRegs.AddRange(call.Args.Select(EmitExpr));
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, builtinName, argRegs));
                return dst;
            }

            // Generic method call: obj.Method(args)
            var methodArgRegs = new List<int> { EmitExpr(mMethod.Target) };
            methodArgRegs.AddRange(call.Args.Select(EmitExpr));
            int methodDst = Alloc();
            Emit(new CallInstr(methodDst, mMethod.Member, methodArgRegs));
            return methodDst;
        }

        // Direct function call: Foo(args)
        if (call.Callee is IdentExpr funcId)
        {
            var argRegs = call.Args.Select(EmitExpr).ToList();
            int dst = Alloc();
            Emit(new CallInstr(dst, funcId.Name, argRegs));
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
