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

            case NullConditionalExpr nc:
                return EmitNullConditional(nc);

            case NullCoalesceExpr nc:
                return EmitNullCoalesce(nc);

            case CastExpr cast:
                return EmitExpr(cast.Operand);

            // Enum member access: Direction.North → ConstI64
            case MemberExpr m when m.Target is IdentExpr enumId
                                && _enumConstants.TryGetValue($"{enumId.Name}.{m.Member}", out long enumVal):
            {
                int dst = Alloc();
                Emit(new ConstI64Instr(dst, enumVal));
                return dst;
            }

            case MemberExpr m when m.Member is "Length" or "Count":
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

            // new Dictionary<K,V>() → __dict_new builtin
            case NewExpr newExpr when newExpr.Type is NamedType { Name: "Dictionary" }:
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__dict_new", []));
                return dst;
            }

            // new List<T>() → __list_new builtin
            case NewExpr newExpr when newExpr.Type is NamedType { Name: "List" }:
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__list_new", []));
                return dst;
            }

            case NewExpr newExpr when newExpr.Type is NamedType nt:
            {
                var argRegs = newExpr.Args.Select(EmitExpr).ToList();
                int dst = Alloc();
                Emit(new ObjNewInstr(dst, QualifyName(nt.Name), argRegs));
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
            "~" => new BitNotInstr(dst, src),
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

    // ── Null coalescing ───────────────────────────────────────────────────────

    private int EmitNullCoalesce(NullCoalesceExpr nc)
    {
        int leftReg  = EmitExpr(nc.Left);
        int nullReg  = Alloc();
        int cmpReg   = Alloc();
        int result   = Alloc();

        Emit(new ConstNullInstr(nullReg));
        Emit(new EqInstr(cmpReg, leftReg, nullReg));

        string nullLbl = FreshLabel("nc_null");
        string endLbl  = FreshLabel("nc_end");
        EndBlock(new BrCondTerm(cmpReg, nullLbl, endLbl));

        // non-null path: result = left
        StartBlock(endLbl);
        Emit(new CopyInstr(result, leftReg));
        string afterNonNull = FreshLabel("nc_after");
        EndBlock(new BrTerm(afterNonNull));

        // null path: result = right
        StartBlock(nullLbl);
        int rightReg = EmitExpr(nc.Right);
        Emit(new CopyInstr(result, rightReg));
        EndBlock(new BrTerm(afterNonNull));

        StartBlock(afterNonNull);
        return result;
    }

    // ── Null-conditional ─────────────────────────────────────────────────────

    /// `target?.member` — null if target is null, else `target.member`
    private int EmitNullConditional(NullConditionalExpr nc)
    {
        int targetReg = EmitExpr(nc.Target);
        int nullReg   = Alloc();
        int cmpReg    = Alloc();
        int result    = Alloc();

        Emit(new ConstNullInstr(nullReg));
        Emit(new EqInstr(cmpReg, targetReg, nullReg));

        string nullLbl    = FreshLabel("nc_null");
        string nonNullLbl = FreshLabel("nc_member");
        string endLbl     = FreshLabel("nc_end");
        EndBlock(new BrCondTerm(cmpReg, nullLbl, nonNullLbl));

        // non-null path: result = target.member
        StartBlock(nonNullLbl);
        int memberReg = Alloc();
        if (nc.Member is "Length" or "Count")
            Emit(new BuiltinInstr(memberReg, "__len", [targetReg]));
        else
            Emit(new FieldGetInstr(memberReg, targetReg, nc.Member));
        Emit(new CopyInstr(result, memberReg));
        EndBlock(new BrTerm(endLbl));

        // null path: result = null
        StartBlock(nullLbl);
        int nullResult = Alloc();
        Emit(new ConstNullInstr(nullResult));
        Emit(new CopyInstr(result, nullResult));
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
            "&"  => new BitAndInstr(dst, a, b),
            "|"  => new BitOrInstr(dst, a, b),
            "^"  => new BitXorInstr(dst, a, b),
            "<<" => new ShlInstr(dst, a, b),
            ">>" => new ShrInstr(dst, a, b),
            _    => throw new NotSupportedException($"operator `{bin.Op}` not yet supported")
        };
        Emit(instr);
        return dst;
    }

    // ── Builtin dispatch tables ───────────────────────────────────────────────
    //   To add a new pseudo-class static builtin: add one entry to StaticBuiltins.
    //   To add a new instance method builtin:     add one entry to InstanceBuiltins.

    /// Pseudo-class static calls: "ClassName.Method" → builtin name.
    private static readonly IReadOnlyDictionary<string, string> StaticBuiltins =
        new Dictionary<string, string>
        {
            // Assert
            ["Assert.Equal"]    = "__assert_eq",
            ["Assert.True"]     = "__assert_true",
            ["Assert.False"]    = "__assert_false",
            ["Assert.Contains"] = "__assert_contains",
            ["Assert.Null"]     = "__assert_null",
            ["Assert.NotNull"]  = "__assert_not_null",
            // Console
            ["Console.WriteLine"] = "__println",
            ["Console.Write"]     = "__print",
            // Math
            ["Math.Abs"]     = "__math_abs",
            ["Math.Max"]     = "__math_max",
            ["Math.Min"]     = "__math_min",
            ["Math.Pow"]     = "__math_pow",
            ["Math.Sqrt"]    = "__math_sqrt",
            ["Math.Floor"]   = "__math_floor",
            ["Math.Ceiling"] = "__math_ceiling",
            ["Math.Round"]   = "__math_round",
        };

    /// Instance method calls: method name → builtin name (receiver becomes first arg).
    /// Contains dispatches at runtime so it works for both string and List<T>.
    private static readonly IReadOnlyDictionary<string, string> InstanceBuiltins =
        new Dictionary<string, string>
        {
            // String
            ["Substring"]  = "__str_substring",
            ["StartsWith"] = "__str_starts_with",
            ["EndsWith"]   = "__str_ends_with",
            ["IndexOf"]    = "__str_index_of",
            ["Replace"]    = "__str_replace",
            ["ToLower"]    = "__str_to_lower",
            ["ToUpper"]    = "__str_to_upper",
            ["Trim"]       = "__str_trim",
            ["TrimStart"]  = "__str_trim_start",
            ["TrimEnd"]    = "__str_trim_end",
            ["Split"]      = "__str_split",
            // Shared: string + List<T>
            ["Contains"]   = "__contains",
            // List<T>
            ["Add"]      = "__list_add",
            ["RemoveAt"] = "__list_remove_at",
            ["Clear"]    = "__list_clear",
            ["Insert"]   = "__list_insert",
            // Dictionary<K,V>
            ["ContainsKey"] = "__dict_contains_key",
            ["Remove"]      = "__dict_remove",
            ["Keys"]        = "__dict_keys",
            ["Values"]      = "__dict_values",
        };

    // ── Call ─────────────────────────────────────────────────────────────────

    private int EmitCall(CallExpr call)
    {
        // ── Pseudo-class static calls: Assert.X, Console.X, Math.X ──────────
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var cls }, Member: var mStatic }
            && StaticBuiltins.TryGetValue($"{cls}.{mStatic}", out var staticBuiltin))
        {
            var argRegs = call.Args.Select(EmitExpr).ToList();
            // Console: concat multiple args into one string argument
            if (cls == "Console" && argRegs.Count != 1)
                argRegs = [EmitConcat(argRegs)];
            return EmitBuiltin(staticBuiltin, argRegs);
        }

        // ── Instance method calls: str.X(), list.X() ─────────────────────────
        if (call.Callee is MemberExpr mInst
            && InstanceBuiltins.TryGetValue(mInst.Member, out var instBuiltin))
        {
            int targetReg = EmitExpr(mInst.Target);
            var argRegs   = new List<int> { targetReg };
            argRegs.AddRange(call.Args.Select(EmitExpr));
            return EmitBuiltin(instBuiltin, argRegs);
        }

        // ── Class method call: obj.Method(args) ──────────────────────────────
        if (call.Callee is MemberExpr mMethod)
        {
            var argRegs = new List<int> { EmitExpr(mMethod.Target) };
            argRegs.AddRange(call.Args.Select(EmitExpr));
            int dst = Alloc();
            Emit(new CallInstr(dst, ResolveMethodName(mMethod.Member), argRegs));
            return dst;
        }

        // ── Free function call: Foo(args) ─────────────────────────────────────
        if (call.Callee is IdentExpr funcId)
        {
            var argRegs = call.Args.Select(EmitExpr).ToList();
            int dst = Alloc();
            // Qualify if it's a top-level function in the same compilation unit
            string callName = _topLevelFunctionNames.Contains(funcId.Name)
                ? QualifyName(funcId.Name)
                : funcId.Name;
            Emit(new CallInstr(dst, callName, argRegs));
            return dst;
        }

        throw new NotSupportedException($"call pattern not supported: {call.Callee.GetType().Name}");
    }

    private int EmitBuiltin(string name, List<int> args)
    {
        int dst = Alloc();
        Emit(new BuiltinInstr(dst, name, args));
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
