using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Bound expression emission — part of FunctionEmitter.
internal sealed partial class FunctionEmitter
{
    // ── Bound expression dispatcher ───────────────────────────────────────────

    private int EmitExpr(BoundExpr expr)
    {
        switch (expr)
        {
            case BoundLitStr s:
            {
                int dst = Alloc();
                Emit(new ConstStrInstr(dst, _gen.Intern(s.Value)));
                return dst;
            }
            case BoundLitInt n:
            {
                int dst = Alloc();
                Emit(new ConstI64Instr(dst, n.Value));
                return dst;
            }
            case BoundLitFloat f:
            {
                int dst = Alloc();
                Emit(new ConstF64Instr(dst, f.Value));
                return dst;
            }
            case BoundLitBool b:
            {
                int dst = Alloc();
                Emit(new ConstBoolInstr(dst, b.Value));
                return dst;
            }
            case BoundLitNull:
            {
                int dst = Alloc();
                Emit(new ConstNullInstr(dst));
                return dst;
            }
            case BoundLitChar c:
            {
                int dst = Alloc();
                Emit(new ConstCharInstr(dst, c.Value));
                return dst;
            }
            case BoundInterpolatedStr interp:
                return EmitInterpolation(interp);

            case BoundIdent id:
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

            case BoundAssign assign:
                return EmitBoundAssign(assign);

            case BoundCall call:
                return EmitBoundCall(call);

            case BoundBinary bin:
                return EmitBoundBinary(bin);

            case BoundUnary u:
                return EmitBoundUnary(u);

            case BoundPostfix post:
                return EmitBoundPostfix(post);

            case BoundConditional ternary:
                return EmitBoundTernary(ternary);

            case BoundNullConditional nc:
                return EmitBoundNullConditional(nc);

            case BoundNullCoalesce nc:
                return EmitBoundNullCoalesce(nc);

            case BoundIsPattern ipe:
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

            case BoundCast cast:
                return EmitExpr(cast.Operand); // cast is a no-op in IR

            case BoundMember m:
                return EmitBoundMember(m);

            case BoundIndex ix:
            {
                int targetReg = EmitExpr(ix.Target);
                int idxReg    = EmitExpr(ix.Index);
                int dst       = Alloc();
                Emit(new ArrayGetInstr(dst, targetReg, idxReg));
                return dst;
            }

            case BoundArrayCreate ac:
            {
                int sizeReg = EmitExpr(ac.Size);
                int dst = Alloc();
                Emit(new ArrayNewInstr(dst, sizeReg));
                return dst;
            }

            case BoundArrayLit al:
            {
                var elemRegs = al.Elements.Select(EmitExpr).ToList();
                int dst = Alloc();
                Emit(new ArrayNewLitInstr(dst, elemRegs));
                return dst;
            }

            case BoundNew n:
                return EmitBoundNew(n);

            case BoundSwitchExpr sw:
                return EmitBoundSwitchExpr(sw);

            case BoundError:
            {
                int dst = Alloc();
                Emit(new ConstNullInstr(dst));
                return dst;
            }

            default:
                throw new NotSupportedException(
                    $"BoundExpr type {expr.GetType().Name} not yet supported in FunctionEmitter");
        }
    }

    // ── Member access ─────────────────────────────────────────────────────────

    private int EmitBoundMember(BoundMember m)
    {
        // Enum constant: BoundIdent with unknown type + name in enum constants
        if (m.Target is BoundIdent enumId
            && _gen._enumConstants.TryGetValue($"{enumId.Name}.{m.MemberName}", out long enumVal))
        {
            int dst = Alloc();
            Emit(new ConstI64Instr(dst, enumVal));
            return dst;
        }

        // Static field: BoundIdent with unknown type + class name in static fields
        if (m.Target is BoundIdent sfId
            && _gen.TryGetStaticFieldKey(sfId.Name, m.MemberName) is { } sfKey)
        {
            int dst = Alloc();
            Emit(new StaticGetInstr(dst, sfKey));
            return dst;
        }

        // Instance field access
        int objReg = EmitExpr(m.Target);
        int dst2 = Alloc();
        Emit(new FieldGetInstr(dst2, objReg, m.MemberName));
        return dst2;
    }

    // ── Assignment ────────────────────────────────────────────────────────────

    private int EmitBoundAssign(BoundAssign assign)
    {
        int valReg = EmitExpr(assign.Value);

        if (assign.Target is BoundIdent id)
        {
            if (_mutableVars.Contains(id.Name))
                Emit(new StoreInstr(id.Name, valReg));
            else if (_instanceFields.Contains(id.Name))
                Emit(new FieldSetInstr(0, id.Name, valReg));
            else
                _locals[id.Name] = valReg;
        }
        else if (assign.Target is BoundIndex ix)
        {
            int arrReg = EmitExpr(ix.Target);
            int idxReg = EmitExpr(ix.Index);
            Emit(new ArraySetInstr(arrReg, idxReg, valReg));
        }
        else if (assign.Target is BoundMember fm)
        {
            // Static field assignment via BoundIdent target
            if (fm.Target is BoundIdent { Name: var aClsName }
                && _gen.TryGetStaticFieldKey(aClsName, fm.MemberName) is { } sfKey)
            {
                Emit(new StaticSetInstr(sfKey, valReg));
            }
            else
            {
                int objReg = EmitExpr(fm.Target);
                Emit(new FieldSetInstr(objReg, fm.MemberName, valReg));
            }
        }

        return valReg;
    }

    // ── Unary / postfix ───────────────────────────────────────────────────────

    private int EmitBoundUnary(BoundUnary u)
    {
        if (u.Op == UnaryOp.Await) return EmitExpr(u.Operand);

        // Static field prefix ++ / --
        if (u.Op is UnaryOp.PrefixInc or UnaryOp.PrefixDec
            && u.Operand is BoundMember { Target: BoundIdent { Name: var ucn }, MemberName: var ufn }
            && _gen.TryGetStaticFieldKey(ucn, ufn) is { } uSfKey)
        {
            int oldReg = Alloc(); Emit(new StaticGetInstr(oldReg, uSfKey));
            int one    = Alloc(); Emit(new ConstI64Instr(one, 1));
            int newReg = Alloc();
            Emit(u.Op == UnaryOp.PrefixInc ? new AddInstr(newReg, oldReg, one) : (IrInstr)new SubInstr(newReg, oldReg, one));
            Emit(new StaticSetInstr(uSfKey, newReg));
            return newReg;
        }

        // Local variable prefix ++ / --
        if (u.Op is UnaryOp.PrefixInc or UnaryOp.PrefixDec && u.Operand is BoundIdent prefixId)
        {
            int oldReg = EmitExpr(u.Operand);
            int one    = Alloc();
            int newReg = Alloc();
            Emit(new ConstI64Instr(one, 1));
            Emit(u.Op == UnaryOp.PrefixInc ? new AddInstr(newReg, oldReg, one)
                                           : (IrInstr)new SubInstr(newReg, oldReg, one));
            WriteBackName(prefixId.Name, newReg);
            return newReg;
        }

        int src = EmitExpr(u.Operand);
        int dst = Alloc();
        Emit(u.Op switch
        {
            UnaryOp.Not            => (IrInstr)new NotInstr(dst, src),
            UnaryOp.Neg            => new NegInstr(dst, src),
            UnaryOp.BitNot         => new BitNotInstr(dst, src),
            UnaryOp.Plus           => new CopyInstr(dst, src),  // unary + is identity
            _                      => new CopyInstr(dst, src)   // PrefixInc/Dec on non-addressable; Await unreachable
        });
        return dst;
    }

    private int EmitBoundPostfix(BoundPostfix post)
    {
        // Static field postfix ++ / --
        if (post.Operand is BoundMember { Target: BoundIdent { Name: var pcn }, MemberName: var pfn }
            && _gen.TryGetStaticFieldKey(pcn, pfn) is { } pSfKey)
        {
            int oldReg = Alloc(); Emit(new StaticGetInstr(oldReg, pSfKey));
            int one    = Alloc(); Emit(new ConstI64Instr(one, 1));
            int newReg = Alloc();
            Emit(post.Op == PostfixOp.Inc ? new AddInstr(newReg, oldReg, one)
                                          : (IrInstr)new SubInstr(newReg, oldReg, one));
            Emit(new StaticSetInstr(pSfKey, newReg));
            return oldReg;
        }

        if (post.Operand is BoundIdent id)
        {
            int oldReg = EmitExpr(post.Operand);
            int one    = Alloc();
            int newReg = Alloc();
            Emit(new ConstI64Instr(one, 1));
            Emit(post.Op == PostfixOp.Inc ? new AddInstr(newReg, oldReg, one)
                                          : (IrInstr)new SubInstr(newReg, oldReg, one));
            WriteBackName(id.Name, newReg);
            return oldReg;
        }
        return EmitExpr(post.Operand);
    }

    // ── Ternary / null operators ──────────────────────────────────────────────

    private int EmitBoundTernary(BoundConditional ternary)
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

    private int EmitBoundNullCoalesce(BoundNullCoalesce nc)
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

    private int EmitBoundNullConditional(BoundNullConditional nc)
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
        Emit(new FieldGetInstr(memberReg, targetReg, nc.MemberName));
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

    private int EmitBoundBinary(BoundBinary bin)
    {
        // is / as require the type name from the right operand
        if (bin.Op is BinaryOp.Is or BinaryOp.As)
        {
            int objReg   = EmitExpr(bin.Left);
            var qualName = bin.Right is BoundIdent ti ? _gen.QualifyName(ti.Name) : "__unknown";
            int dst      = Alloc();
            Emit(bin.Op == BinaryOp.Is
                ? new IsInstanceInstr(dst, objReg, qualName)
                : (IrInstr)new AsCastInstr(dst, objReg, qualName));
            return dst;
        }

        int a   = EmitExpr(bin.Left);
        int b   = EmitExpr(bin.Right);
        int dst2 = Alloc();
        IrInstr instr = bin.Op switch
        {
            BinaryOp.Add    => new AddInstr(dst2, a, b),
            BinaryOp.Sub    => new SubInstr(dst2, a, b),
            BinaryOp.Mul    => new MulInstr(dst2, a, b),
            BinaryOp.Div    => new DivInstr(dst2, a, b),
            BinaryOp.Rem    => new RemInstr(dst2, a, b),
            BinaryOp.Eq     => new EqInstr(dst2, a, b),
            BinaryOp.Ne     => new NeInstr(dst2, a, b),
            BinaryOp.Lt     => new LtInstr(dst2, a, b),
            BinaryOp.Le     => new LeInstr(dst2, a, b),
            BinaryOp.Gt     => new GtInstr(dst2, a, b),
            BinaryOp.Ge     => new GeInstr(dst2, a, b),
            BinaryOp.And    => new AndInstr(dst2, a, b),
            BinaryOp.Or     => new OrInstr(dst2, a, b),
            BinaryOp.BitAnd => new BitAndInstr(dst2, a, b),
            BinaryOp.BitOr  => new BitOrInstr(dst2, a, b),
            BinaryOp.BitXor => new BitXorInstr(dst2, a, b),
            BinaryOp.Shl    => new ShlInstr(dst2, a, b),
            BinaryOp.Shr    => new ShrInstr(dst2, a, b),
            BinaryOp.Is or BinaryOp.As => throw new InvalidOperationException("unreachable"),
        };
        Emit(instr);
        return dst2;
    }

    // ── New object ────────────────────────────────────────────────────────────

    private int EmitBoundNew(BoundNew n)
    {
        switch (n.QualName)
        {
            case "StringBuilder":
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__sb_new", []));
                return dst;
            }
            case "Dictionary":
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__dict_new", []));
                return dst;
            }
            case "List":
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__list_new", []));
                return dst;
            }
            default:
            {
                var argRegs = n.Args.Select(EmitExpr).ToList();
                string ctorKey = $"{_gen.QualifyName(n.QualName)}.{n.QualName}";
                argRegs = FillDefaults(ctorKey, argRegs);
                int dst = Alloc();
                Emit(new ObjNewInstr(dst, _gen.QualifyName(n.QualName), argRegs));
                return dst;
            }
        }
    }
}
