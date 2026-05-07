using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

internal sealed partial class FunctionEmitter
{

    // ── Unary / postfix ───────────────────────────────────────────────────────

    private TypedReg EmitBoundUnary(BoundUnary u)
    {
        if (u.Op == UnaryOp.Await) return EmitExpr(u.Operand);

        // Static field prefix ++ / --
        if (u.Op is UnaryOp.PrefixInc or UnaryOp.PrefixDec
            && u.Operand is BoundMember { Target: BoundIdent { Name: var ucn }, MemberName: var ufn }
            && _ctx.TryGetStaticFieldKey(ucn, ufn) is { } uSfKey)
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
            && _ctx.TryGetStaticFieldKey(pcn, pfn) is { } pSfKey)
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
            // Save the old value to a new register before WriteBackName overwrites it
            var savedOldReg = Alloc(ToIrType(post.Type));
            Emit(new CopyInstr(savedOldReg, oldReg));

            var one    = Alloc(ToIrType(post.Type));
            var newReg = Alloc(ToIrType(post.Type));
            Emit(new ConstI64Instr(one, 1));
            Emit(post.Op == PostfixOp.Inc ? new AddInstr(newReg, oldReg, one)
                                          : (IrInstr)new SubInstr(newReg, oldReg, one));
            WriteBackName(id.Name, newReg);
            return savedOldReg;  // Return the saved old value, not the variable register
        }
        return EmitExpr(post.Operand);
    }
}
