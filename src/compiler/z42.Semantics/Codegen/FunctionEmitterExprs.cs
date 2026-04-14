using Z42.Core.Text;
using Z42.Syntax.Parser;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Expression emission — part of the FunctionEmitter partial class.
internal sealed partial class FunctionEmitter
{
    // ── Expressions ──────────────────────────────────────────────────────────

    private int EmitExpr(Expr expr)
    {
        switch (expr)
        {
            case LitStrExpr s:
            {
                int dst = Alloc();
                Emit(new ConstStrInstr(dst, _gen.Intern(s.Value)));
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
                Emit(new ConstCharInstr(dst, c.Value));
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
                if (_instanceFields.Contains(id.Name))
                {
                    int dst = Alloc();
                    Emit(new FieldGetInstr(dst, 0, id.Name));
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

            case IsPatternExpr ipe:
            {
                int objReg  = EmitExpr(ipe.Target);
                int boolReg = Alloc();
                var qualName = _gen.QualifyName(ipe.TypeName);
                Emit(new IsInstanceInstr(boolReg, objReg, qualName));
                int castReg = Alloc();
                Emit(new AsCastInstr(castReg, objReg, qualName));
                _locals[ipe.Binding] = castReg;
                return boolReg;
            }

            case CastExpr cast:
                return EmitExpr(cast.Operand);

            case MemberExpr m when m.Target is IdentExpr enumId
                                && _gen._enumConstants.TryGetValue(
                                    $"{enumId.Name}.{m.Member}", out long enumVal):
            {
                int dst = Alloc();
                Emit(new ConstI64Instr(dst, enumVal));
                return dst;
            }

            case MemberExpr m when m.Target is IdentExpr { Name: var clsName }
                && _gen.TryGetStaticFieldKey(clsName, m.Member) is { } sfKey:
            {
                int dst = Alloc();
                Emit(new StaticGetInstr(dst, sfKey));
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

            case NewExpr when expr is NewExpr { Type: NamedType { Name: "StringBuilder" } }:
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__sb_new", []));
                return dst;
            }

            case NewExpr newExpr when newExpr.Type is NamedType { Name: "Dictionary" }:
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__dict_new", []));
                return dst;
            }

            case NewExpr newExpr when newExpr.Type is NamedType { Name: "List" }:
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__list_new", []));
                return dst;
            }

            case NewExpr newExpr when newExpr.Type is NamedType nt:
            {
                var argRegs = newExpr.Args.Select(EmitExpr).ToList();
                string ctorKey = $"{_gen.QualifyName(nt.Name)}.{nt.Name}";
                argRegs = FillDefaults(ctorKey, argRegs);
                int dst = Alloc();
                Emit(new ObjNewInstr(dst, _gen.QualifyName(nt.Name), argRegs));
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
            else if (_instanceFields.Contains(id.Name))
                Emit(new FieldSetInstr(0, id.Name, valReg));
            else
                _locals[id.Name] = valReg;
        }
        else if (assign.Target is IndexExpr ix)
        {
            int arrReg = EmitExpr(ix.Target);
            int idxReg = EmitExpr(ix.Index);
            Emit(new ArraySetInstr(arrReg, idxReg, valReg));
        }
        else if (assign.Target is MemberExpr { Target: IdentExpr { Name: var aClsName }, Member: var aField }
            && _gen.TryGetStaticFieldKey(aClsName, aField) is { } sfKey)
        {
            Emit(new StaticSetInstr(sfKey, valReg));
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

        if (u.Op is "++" or "--" && u.Operand is MemberExpr { Target: IdentExpr { Name: var ucn }, Member: var ufn }
            && _gen.TryGetStaticFieldKey(ucn, ufn) is { } uSfKey)
        {
            int oldReg = Alloc(); Emit(new StaticGetInstr(oldReg, uSfKey));
            int one    = Alloc(); Emit(new ConstI64Instr(one, 1));
            int newReg = Alloc();
            Emit(u.Op == "++" ? new AddInstr(newReg, oldReg, one) : (IrInstr)new SubInstr(newReg, oldReg, one));
            Emit(new StaticSetInstr(uSfKey, newReg));
            return newReg;
        }

        if (u.Op is "++" or "--" && u.Operand is IdentExpr prefixId)
        {
            int oldReg = EmitExpr(u.Operand);
            int one    = Alloc();
            int newReg = Alloc();
            Emit(new ConstI64Instr(one, 1));
            Emit(u.Op == "++" ? new AddInstr(newReg, oldReg, one)
                              : (IrInstr)new SubInstr(newReg, oldReg, one));
            WriteBackName(prefixId.Name, newReg);
            return newReg;
        }

        int src = EmitExpr(u.Operand);
        int dst = Alloc();
        Emit(u.Op switch
        {
            "!" => (IrInstr)new NotInstr(dst, src),
            "-" => new NegInstr(dst, src),
            "~" => new BitNotInstr(dst, src),
            _   => new CopyInstr(dst, src)
        });
        return dst;
    }

    private int EmitPostfix(PostfixExpr post)
    {
        if (post.Operand is MemberExpr { Target: IdentExpr { Name: var pcn }, Member: var pfn }
            && _gen.TryGetStaticFieldKey(pcn, pfn) is { } pSfKey)
        {
            int oldReg = Alloc(); Emit(new StaticGetInstr(oldReg, pSfKey));
            int one    = Alloc(); Emit(new ConstI64Instr(one, 1));
            int newReg = Alloc();
            Emit(post.Op == "++" ? new AddInstr(newReg, oldReg, one)
                                 : (IrInstr)new SubInstr(newReg, oldReg, one));
            Emit(new StaticSetInstr(pSfKey, newReg));
            return oldReg;
        }

        if (post.Operand is IdentExpr id)
        {
            int oldReg = EmitExpr(post.Operand);
            int one    = Alloc();
            int newReg = Alloc();
            Emit(new ConstI64Instr(one, 1));
            Emit(post.Op == "++" ? new AddInstr(newReg, oldReg, one)
                                 : (IrInstr)new SubInstr(newReg, oldReg, one));
            WriteBackName(id.Name, newReg);
            return oldReg;
        }
        return EmitExpr(post.Operand);
    }

    // ── Ternary / null operators ─────────────────────────────────────────────

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

        StartBlock(endLbl);
        Emit(new CopyInstr(result, leftReg));
        string afterNonNull = FreshLabel("nc_after");
        EndBlock(new BrTerm(afterNonNull));

        StartBlock(nullLbl);
        int rightReg = EmitExpr(nc.Right);
        Emit(new CopyInstr(result, rightReg));
        EndBlock(new BrTerm(afterNonNull));

        StartBlock(afterNonNull);
        return result;
    }

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

        StartBlock(nonNullLbl);
        int memberReg = Alloc();
        Emit(new FieldGetInstr(memberReg, targetReg, nc.Member));
        Emit(new CopyInstr(result, memberReg));
        EndBlock(new BrTerm(endLbl));

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
        if (bin.Op is "is" or "as" && bin.Right is IdentExpr typeIdent)
        {
            int objReg  = EmitExpr(bin.Left);
            int typeReg = Alloc();
            var qualName = _gen.QualifyName(typeIdent.Name);
            Emit(bin.Op == "is"
                ? new IsInstanceInstr(typeReg, objReg, qualName)
                : (IrInstr)new AsCastInstr(typeReg, objReg, qualName));
            return typeReg;
        }

        if (bin.Op == "is")
        {
            int objReg  = EmitExpr(bin.Left);
            int typeReg = Alloc();
            Emit(new IsInstanceInstr(typeReg, objReg,
                bin.Right is IdentExpr ti ? _gen.QualifyName(ti.Name) : "__unknown"));
            return typeReg;
        }

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
}
