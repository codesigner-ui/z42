using Z42.Compiler.Parser;
using Z42.IR;

namespace Z42.Compiler.Codegen;

/// Expression, call, and string interpolation emission — part of the IrGen partial class.
public sealed partial class IrGen
{
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
                int objReg = EmitExpr(m.Target);
                int dst = Alloc();
                Emit(new FieldGetInstr(dst, objReg, m.Member));
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

            case NewExpr newExpr when newExpr.Type is NamedType nt:
            {
                var argRegs = newExpr.Args.Select(EmitExpr).ToList();
                int dst = Alloc();
                Emit(new ObjNewInstr(dst, nt.Name, argRegs));
                return dst;
            }

            case SwitchExpr sw:
                return EmitSwitchExpr(sw);

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
        else if (assign.Target is MemberExpr fm)
        {
            int objReg = EmitExpr(fm.Target);
            Emit(new FieldSetInstr(objReg, fm.Member, valReg));
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

    // ── Call ─────────────────────────────────────────────────────────────────

    private int EmitCall(CallExpr call)
    {
        // Assert.XXX  →  builtin __assert_*
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: "Assert" }, Member: var assertMember })
        {
            string? assertBuiltin = assertMember switch
            {
                "Equal"    => "__assert_eq",
                "True"     => "__assert_true",
                "False"    => "__assert_false",
                "Contains" => "__assert_contains",
                "Null"     => "__assert_null",
                "NotNull"  => "__assert_not_null",
                _          => null
            };
            if (assertBuiltin != null)
            {
                var argRegs = call.Args.Select(EmitExpr).ToList();
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, assertBuiltin, argRegs));
                return dst;
            }
        }

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

        // Math.XXX  →  builtin __math_*
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: "Math" }, Member: var mathMember })
        {
            string? mathBuiltin = mathMember switch
            {
                "Abs"     => "__math_abs",
                "Max"     => "__math_max",
                "Min"     => "__math_min",
                "Pow"     => "__math_pow",
                "Sqrt"    => "__math_sqrt",
                "Floor"   => "__math_floor",
                "Ceiling" => "__math_ceiling",
                "Round"   => "__math_round",
                _         => null
            };
            if (mathBuiltin != null)
            {
                var argRegs = call.Args.Select(EmitExpr).ToList();
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, mathBuiltin, argRegs));
                return dst;
            }
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
                "IndexOf"    => "__str_index_of",
                "Replace"    => "__str_replace",
                "ToLower"    => "__str_to_lower",
                "ToUpper"    => "__str_to_upper",
                "Trim"       => "__str_trim",
                "TrimStart"  => "__str_trim_start",
                "TrimEnd"    => "__str_trim_end",
                "Split"      => "__str_split",
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

            // Class method call or generic method call
            var methodArgRegs = new List<int> { EmitExpr(mMethod.Target) };
            methodArgRegs.AddRange(call.Args.Select(EmitExpr));
            int methodDst = Alloc();
            // Resolve to qualified name if we know the receiver type
            string methodName = ResolveMethodName(mMethod.Member);
            Emit(new CallInstr(methodDst, methodName, methodArgRegs));
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

    // ── Switch expression ─────────────────────────────────────────────────────

    /// Compiles `subject switch { p1 => e1, p2 => e2, _ => e3 }` as an if-else chain.
    private int EmitSwitchExpr(SwitchExpr sw)
    {
        int subjReg = EmitExpr(sw.Subject);
        int result  = Alloc();
        string endLbl = FreshLabel("sw_end");

        foreach (var arm in sw.Arms)
        {
            if (arm.Pattern == null)
            {
                // default arm: unconditionally emit body
                int defReg = EmitExpr(arm.Body);
                Emit(new CopyInstr(result, defReg));
                EndBlock(new BrTerm(endLbl));
                break;
            }

            string thenLbl = FreshLabel("sw_arm");
            string nextLbl = FreshLabel("sw_next");

            int patReg  = EmitExpr(arm.Pattern);
            int cmpReg  = Alloc();
            Emit(new EqInstr(cmpReg, subjReg, patReg));
            EndBlock(new BrCondTerm(cmpReg, thenLbl, nextLbl));

            StartBlock(thenLbl);
            int bodyReg = EmitExpr(arm.Body);
            Emit(new CopyInstr(result, bodyReg));
            EndBlock(new BrTerm(endLbl));

            StartBlock(nextLbl);
        }

        // If no arm matched and no default, fall through to end
        if (!_blockEnded) EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }
}
