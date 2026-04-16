using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Bound expression emission — part of FunctionEmitter.
internal sealed partial class FunctionEmitter
{
    // ── Bound expression dispatcher ───────────────────────────────────────────

    private TypedReg EmitExpr(BoundExpr expr)
    {
        switch (expr)
        {
            case BoundLitStr s:
            {
                var dst = Alloc(IrType.Str);
                Emit(new ConstStrInstr(dst, _gen.Intern(s.Value)));
                return dst;
            }
            case BoundLitInt n:
            {
                var dst = Alloc(ToIrType(n.Type));
                // Always emit I64: VM creates Value::I64 for all ints.
                // TypedReg.Type carries the narrower type for future typed dispatch.
                Emit(new ConstI64Instr(dst, n.Value));
                return dst;
            }
            case BoundLitFloat f:
            {
                var dst = Alloc(ToIrType(f.Type));
                Emit(new ConstF64Instr(dst, f.Value));
                return dst;
            }
            case BoundLitBool b:
            {
                var dst = Alloc(IrType.Bool);
                Emit(new ConstBoolInstr(dst, b.Value));
                return dst;
            }
            case BoundLitNull:
            {
                var dst = Alloc(IrType.Ref);
                Emit(new ConstNullInstr(dst));
                return dst;
            }
            case BoundLitChar c:
            {
                var dst = Alloc(IrType.Char);
                Emit(new ConstCharInstr(dst, c.Value));
                return dst;
            }
            case BoundInterpolatedStr interp:
                return EmitInterpolation(interp);

            case BoundIdent id:
            {
                if (_locals.TryGetValue(id.Name, out var reg))
                    return reg;
                if (_mutableVars.Contains(id.Name))
                {
                    var dst = Alloc(ToIrType(id.Type));
                    Emit(new LoadInstr(dst, id.Name));
                    return dst;
                }
                if (_instanceFields.Contains(id.Name))
                {
                    var dst = Alloc(ToIrType(id.Type));
                    Emit(new FieldGetInstr(dst, new TypedReg(0, IrType.Ref), id.Name));
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
                var objReg  = EmitExpr(ipe.Target);
                var boolReg = Alloc(IrType.Bool);
                var qualName = _gen.QualifyName(ipe.TypeName);
                Emit(new IsInstanceInstr(boolReg, objReg, qualName));
                var castReg = Alloc(IrType.Ref);
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
                var targetReg = EmitExpr(ix.Target);
                var idxReg    = EmitExpr(ix.Index);
                var dst       = Alloc(ToIrType(ix.Type));
                Emit(new ArrayGetInstr(dst, targetReg, idxReg));
                return dst;
            }

            case BoundArrayCreate ac:
            {
                var sizeReg = EmitExpr(ac.Size);
                var dst = Alloc(IrType.Ref);
                Emit(new ArrayNewInstr(dst, sizeReg));
                return dst;
            }

            case BoundArrayLit al:
            {
                var elemRegs = al.Elements.Select(EmitExpr).ToList();
                var dst = Alloc(IrType.Ref);
                Emit(new ArrayNewLitInstr(dst, elemRegs));
                return dst;
            }

            case BoundNew n:
                return EmitBoundNew(n);

            case BoundSwitchExpr sw:
                return EmitBoundSwitchExpr(sw);

            case BoundError:
            {
                var dst = Alloc(IrType.Ref);
                Emit(new ConstNullInstr(dst));
                return dst;
            }

            default:
                throw new NotSupportedException(
                    $"BoundExpr type {expr.GetType().Name} not yet supported in FunctionEmitter");
        }
    }

    // ── Member access ─────────────────────────────────────────────────────────

    private TypedReg EmitBoundMember(BoundMember m)
    {
        // Enum constant: BoundIdent with unknown type + name in enum constants
        if (m.Target is BoundIdent enumId
            && _gen._enumConstants.TryGetValue($"{enumId.Name}.{m.MemberName}", out long enumVal))
        {
            var dst = Alloc(IrType.I64);
            Emit(new ConstI64Instr(dst, enumVal));
            return dst;
        }

        // Static field: BoundIdent with unknown type + class name in static fields
        if (m.Target is BoundIdent sfId
            && _gen.TryGetStaticFieldKey(sfId.Name, m.MemberName) is { } sfKey)
        {
            var dst = Alloc(ToIrType(m.Type));
            Emit(new StaticGetInstr(dst, sfKey));
            return dst;
        }

        // Instance field access
        var objReg = EmitExpr(m.Target);
        var dst2 = Alloc(ToIrType(m.Type));
        Emit(new FieldGetInstr(dst2, objReg, m.MemberName));
        return dst2;
    }

    // ── Assignment ────────────────────────────────────────────────────────────

    private TypedReg EmitBoundAssign(BoundAssign assign)
    {
        var valReg = EmitExpr(assign.Value);

        if (assign.Target is BoundIdent id)
        {
            if (_mutableVars.Contains(id.Name))
                Emit(new StoreInstr(id.Name, valReg));
            else if (_instanceFields.Contains(id.Name))
                Emit(new FieldSetInstr(new TypedReg(0, IrType.Ref), id.Name, valReg));
            else
                _locals[id.Name] = valReg;
        }
        else if (assign.Target is BoundIndex ix)
        {
            var arrReg = EmitExpr(ix.Target);
            var idxReg = EmitExpr(ix.Index);
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
                var objReg = EmitExpr(fm.Target);
                Emit(new FieldSetInstr(objReg, fm.MemberName, valReg));
            }
        }

        return valReg;
    }

    // ── Unary / postfix ───────────────────────────────────────────────────────

    private TypedReg EmitBoundUnary(BoundUnary u)
    {
        if (u.Op == UnaryOp.Await) return EmitExpr(u.Operand);

        // Static field prefix ++ / --
        if (u.Op is UnaryOp.PrefixInc or UnaryOp.PrefixDec
            && u.Operand is BoundMember { Target: BoundIdent { Name: var ucn }, MemberName: var ufn }
            && _gen.TryGetStaticFieldKey(ucn, ufn) is { } uSfKey)
        {
            var oldReg = Alloc(ToIrType(u.Type)); Emit(new StaticGetInstr(oldReg, uSfKey));
            var one    = Alloc(ToIrType(u.Type)); Emit(new ConstI64Instr(one, 1));
            var newReg = Alloc(ToIrType(u.Type));
            Emit(u.Op == UnaryOp.PrefixInc ? new AddInstr(newReg, oldReg, one) : (IrInstr)new SubInstr(newReg, oldReg, one));
            Emit(new StaticSetInstr(uSfKey, newReg));
            return newReg;
        }

        // Local variable prefix ++ / --
        if (u.Op is UnaryOp.PrefixInc or UnaryOp.PrefixDec && u.Operand is BoundIdent prefixId)
        {
            var oldReg = EmitExpr(u.Operand);
            var one    = Alloc(ToIrType(u.Type));
            var newReg = Alloc(ToIrType(u.Type));
            Emit(new ConstI64Instr(one, 1));
            Emit(u.Op == UnaryOp.PrefixInc ? new AddInstr(newReg, oldReg, one)
                                           : (IrInstr)new SubInstr(newReg, oldReg, one));
            WriteBackName(prefixId.Name, newReg);
            return newReg;
        }

        var src = EmitExpr(u.Operand);
        var dst = Alloc(ToIrType(u.Type));
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

    private TypedReg EmitBoundPostfix(BoundPostfix post)
    {
        // Static field postfix ++ / --
        if (post.Operand is BoundMember { Target: BoundIdent { Name: var pcn }, MemberName: var pfn }
            && _gen.TryGetStaticFieldKey(pcn, pfn) is { } pSfKey)
        {
            var oldReg = Alloc(ToIrType(post.Type)); Emit(new StaticGetInstr(oldReg, pSfKey));
            var one    = Alloc(ToIrType(post.Type)); Emit(new ConstI64Instr(one, 1));
            var newReg = Alloc(ToIrType(post.Type));
            Emit(post.Op == PostfixOp.Inc ? new AddInstr(newReg, oldReg, one)
                                          : (IrInstr)new SubInstr(newReg, oldReg, one));
            Emit(new StaticSetInstr(pSfKey, newReg));
            return oldReg;
        }

        if (post.Operand is BoundIdent id)
        {
            var oldReg = EmitExpr(post.Operand);
            var one    = Alloc(ToIrType(post.Type));
            var newReg = Alloc(ToIrType(post.Type));
            Emit(new ConstI64Instr(one, 1));
            Emit(post.Op == PostfixOp.Inc ? new AddInstr(newReg, oldReg, one)
                                          : (IrInstr)new SubInstr(newReg, oldReg, one));
            WriteBackName(id.Name, newReg);
            return oldReg;
        }
        return EmitExpr(post.Operand);
    }

    // ── Ternary / null operators ──────────────────────────────────────────────

    private TypedReg EmitBoundTernary(BoundConditional ternary)
    {
        var condReg    = EmitExpr(ternary.Cond);
        string thenLbl = FreshLabel("tern_then");
        string elseLbl = FreshLabel("tern_else");
        string endLbl  = FreshLabel("tern_end");
        var result = Alloc(ToIrType(ternary.Type));

        EndBlock(new BrCondTerm(condReg, thenLbl, elseLbl));

        StartBlock(thenLbl);
        var thenReg = EmitExpr(ternary.Then);
        Emit(new CopyInstr(result, thenReg));
        EndBlock(new BrTerm(endLbl));

        StartBlock(elseLbl);
        var elseReg = EmitExpr(ternary.Else);
        Emit(new CopyInstr(result, elseReg));
        EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }

    private TypedReg EmitBoundNullCoalesce(BoundNullCoalesce nc)
    {
        var leftReg  = EmitExpr(nc.Left);
        var nullReg  = Alloc(IrType.Ref);
        var cmpReg   = Alloc(IrType.Bool);
        var result   = Alloc(ToIrType(nc.Type));

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
        var rightReg = EmitExpr(nc.Right);
        Emit(new CopyInstr(result, rightReg));
        EndBlock(new BrTerm(afterNonNull));

        StartBlock(afterNonNull);
        return result;
    }

    private TypedReg EmitBoundNullConditional(BoundNullConditional nc)
    {
        var targetReg = EmitExpr(nc.Target);
        var nullReg   = Alloc(IrType.Ref);
        var cmpReg    = Alloc(IrType.Bool);
        var result    = Alloc(ToIrType(nc.Type));

        Emit(new ConstNullInstr(nullReg));
        Emit(new EqInstr(cmpReg, targetReg, nullReg));

        string nullLbl    = FreshLabel("nc_null");
        string nonNullLbl = FreshLabel("nc_member");
        string endLbl     = FreshLabel("nc_end");
        EndBlock(new BrCondTerm(cmpReg, nullLbl, nonNullLbl));

        StartBlock(nonNullLbl);
        var memberReg = Alloc(ToIrType(nc.Type));
        Emit(new FieldGetInstr(memberReg, targetReg, nc.MemberName));
        Emit(new CopyInstr(result, memberReg));
        EndBlock(new BrTerm(endLbl));

        StartBlock(nullLbl);
        var nullResult = Alloc(IrType.Ref);
        Emit(new ConstNullInstr(nullResult));
        Emit(new CopyInstr(result, nullResult));
        EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }

    // ── Binary ────────────────────────────────────────────────────────────────

    /// Instruction factory: BinaryOp → constructor. One entry per arithmetic/comparison/logic op.
    private static readonly Dictionary<BinaryOp, Func<TypedReg, TypedReg, TypedReg, IrInstr>>
        BinFactory = new()
    {
        [BinaryOp.Add]    = (d, a, b) => new AddInstr(d, a, b),
        [BinaryOp.Sub]    = (d, a, b) => new SubInstr(d, a, b),
        [BinaryOp.Mul]    = (d, a, b) => new MulInstr(d, a, b),
        [BinaryOp.Div]    = (d, a, b) => new DivInstr(d, a, b),
        [BinaryOp.Rem]    = (d, a, b) => new RemInstr(d, a, b),
        [BinaryOp.Eq]     = (d, a, b) => new EqInstr(d, a, b),
        [BinaryOp.Ne]     = (d, a, b) => new NeInstr(d, a, b),
        [BinaryOp.Lt]     = (d, a, b) => new LtInstr(d, a, b),
        [BinaryOp.Le]     = (d, a, b) => new LeInstr(d, a, b),
        [BinaryOp.Gt]     = (d, a, b) => new GtInstr(d, a, b),
        [BinaryOp.Ge]     = (d, a, b) => new GeInstr(d, a, b),
        [BinaryOp.And]    = (d, a, b) => new AndInstr(d, a, b),
        [BinaryOp.Or]     = (d, a, b) => new OrInstr(d, a, b),
        [BinaryOp.BitAnd] = (d, a, b) => new BitAndInstr(d, a, b),
        [BinaryOp.BitOr]  = (d, a, b) => new BitOrInstr(d, a, b),
        [BinaryOp.BitXor] = (d, a, b) => new BitXorInstr(d, a, b),
        [BinaryOp.Shl]    = (d, a, b) => new ShlInstr(d, a, b),
        [BinaryOp.Shr]    = (d, a, b) => new ShrInstr(d, a, b),
    };

    private TypedReg EmitBoundBinary(BoundBinary bin)
    {
        // is / as require the type name from the right operand
        if (bin.Op is BinaryOp.Is or BinaryOp.As)
        {
            var objReg   = EmitExpr(bin.Left);
            var qualName = bin.Right is BoundIdent ti ? _gen.QualifyName(ti.Name) : "__unknown";
            var dst      = Alloc(bin.Op == BinaryOp.Is ? IrType.Bool : IrType.Ref);
            Emit(bin.Op == BinaryOp.Is
                ? new IsInstanceInstr(dst, objReg, qualName)
                : (IrInstr)new AsCastInstr(dst, objReg, qualName));
            return dst;
        }

        var a   = EmitExpr(bin.Left);
        var b   = EmitExpr(bin.Right);
        var dst2 = Alloc(ToIrType(bin.Type));
        Emit(BinFactory[bin.Op](dst2, a, b));
        return dst2;
    }

    // ── New object ────────────────────────────────────────────────────────────

    private TypedReg EmitBoundNew(BoundNew n)
    {
        switch (n.QualName)
        {
            case "StringBuilder":
            {
                var dst = Alloc(IrType.Ref);
                Emit(new BuiltinInstr(dst, "__sb_new", []));
                return dst;
            }
            case "Dictionary":
            {
                var dst = Alloc(IrType.Ref);
                Emit(new BuiltinInstr(dst, "__dict_new", []));
                return dst;
            }
            case "List":
            {
                var dst = Alloc(IrType.Ref);
                Emit(new BuiltinInstr(dst, "__list_new", []));
                return dst;
            }
            default:
            {
                var argRegs = n.Args.Select(EmitExpr).ToList();
                string ctorKey = $"{_gen.QualifyName(n.QualName)}.{n.QualName}";
                argRegs = FillDefaults(ctorKey, argRegs);
                var dst = Alloc(IrType.Ref);
                Emit(new ObjNewInstr(dst, _gen.QualifyName(n.QualName), argRegs));
                return dst;
            }
        }
    }
}
