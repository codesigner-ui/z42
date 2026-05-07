using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

internal sealed partial class FunctionEmitter
{

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
}
