using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

internal sealed partial class FunctionEmitter
{

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
            // QualifyClassName: resolves imported class to its source namespace
            // (e.g. TestFailure → Std.TestFailure when the test CU imports it).
            var qualName = bin.Right is BoundIdent ti ? _ctx.QualifyClassName(ti.Name) : "__unknown";
            var dst      = Alloc(bin.Op == BinaryOp.Is ? IrType.Bool : IrType.Ref);
            Emit(bin.Op == BinaryOp.Is
                ? new IsInstanceInstr(dst, objReg, qualName)
                : (IrInstr)new AsCastInstr(dst, objReg, qualName));
            return dst;
        }

        // Short-circuit `&&` / `||`: right side only evaluated when left doesn't decide.
        if (bin.Op is BinaryOp.And or BinaryOp.Or)
            return EmitShortCircuit(bin);

        var a   = EmitExpr(bin.Left);
        var b   = EmitExpr(bin.Right);
        var dst2 = Alloc(ToIrType(bin.Type));
        Emit(BinFactory[bin.Op](dst2, a, b));
        return dst2;
    }

    /// Desugar `a && b` / `a || b` into BrCond blocks so `b` is skipped when `a` decides.
    /// `a && b` : if a then eval b else false.
    /// `a || b` : if a then true else eval b.
    private TypedReg EmitShortCircuit(BoundBinary bin)
    {
        bool isAnd = bin.Op == BinaryOp.And;
        string tag = isAnd ? "and" : "or";
        string rhsLbl   = FreshLabel($"{tag}_rhs");
        string shortLbl = FreshLabel($"{tag}_short");
        string endLbl   = FreshLabel($"{tag}_end");
        var result = Alloc(IrType.Bool);

        var leftReg = EmitExpr(bin.Left);
        // And: truthy → evaluate RHS; falsy → short-circuit to false.
        // Or : truthy → short-circuit to true; falsy → evaluate RHS.
        EndBlock(isAnd
            ? new BrCondTerm(leftReg, rhsLbl, shortLbl)
            : new BrCondTerm(leftReg, shortLbl, rhsLbl));

        StartBlock(rhsLbl);
        var rightReg = EmitExpr(bin.Right);
        Emit(new CopyInstr(result, rightReg));
        EndBlock(new BrTerm(endLbl));

        StartBlock(shortLbl);
        var constReg = Alloc(IrType.Bool);
        Emit(new ConstBoolInstr(constReg, !isAnd));
        Emit(new CopyInstr(result, constReg));
        EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }
}
