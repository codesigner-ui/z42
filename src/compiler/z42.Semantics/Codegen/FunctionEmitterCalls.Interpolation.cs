using Z42.Semantics.Bound;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// String interpolation + switch expression emission. spec
/// split-emitter-stmts-calls (2026-05-12): extracted from FunctionEmitterCalls.cs
/// to keep the main file under the 300 LOC soft limit. Zero behavior change.
internal sealed partial class FunctionEmitter
{
    // ── String interpolation ──────────────────────────────────────────────────

    private TypedReg EmitInterpolation(BoundInterpolatedStr interp)
    {
        if (interp.Parts.Count == 0)
        {
            var dst = Alloc(IrType.Str);
            Emit(new ConstStrInstr(dst, _ctx.Intern("")));
            return dst;
        }
        var partRegs = interp.Parts.Select(EmitBoundPart).ToList();
        return EmitConcat(partRegs);
    }

    private TypedReg EmitBoundPart(BoundInterpolationPart part) => part switch
    {
        BoundTextPart tp => EmitBoundTextPart(tp),
        BoundExprPart ep => EmitBoundExprPart(ep),
        _ => throw new NotSupportedException(part.GetType().Name)
    };

    private TypedReg EmitBoundTextPart(BoundTextPart tp)
    {
        var dst = Alloc(IrType.Str);
        Emit(new ConstStrInstr(dst, _ctx.Intern(tp.Text)));
        return dst;
    }

    private TypedReg EmitBoundExprPart(BoundExprPart ep)
    {
        var exprReg = EmitExpr(ep.Inner);
        bool isStringLit = ep.Inner is BoundLitStr or BoundInterpolatedStr;
        if (!isStringLit)
        {
            var strReg = Alloc(IrType.Str);
            Emit(new ToStrInstr(strReg, exprReg));
            return strReg;
        }
        return exprReg;
    }

    private TypedReg EmitConcat(List<TypedReg> regs)
    {
        var result = regs[0];
        for (int i = 1; i < regs.Count; i++)
        {
            var dst = Alloc(IrType.Str);
            Emit(new StrConcatInstr(dst, result, regs[i]));
            result = dst;
        }
        return result;
    }

    // ── Switch expression ─────────────────────────────────────────────────────

    private TypedReg EmitBoundSwitchExpr(BoundSwitchExpr sw)
    {
        var subjReg = EmitExpr(sw.Subject);
        var result  = Alloc(ToIrType(sw.Type));
        string endLbl = FreshLabel("sw_end");

        foreach (var arm in sw.Arms)
        {
            if (arm.Pattern == null)
            {
                var defReg = EmitExpr(arm.Body);
                Emit(new CopyInstr(result, defReg));
                EndBlock(new BrTerm(endLbl));
                break;
            }

            string thenLbl = FreshLabel("sw_arm");
            string nextLbl = FreshLabel("sw_next");

            var patReg  = EmitExpr(arm.Pattern);
            var cmpReg  = Alloc(IrType.Bool);
            Emit(new EqInstr(cmpReg, subjReg, patReg));
            EndBlock(new BrCondTerm(cmpReg, thenLbl, nextLbl));

            StartBlock(thenLbl);
            var bodyReg = EmitExpr(arm.Body);
            Emit(new CopyInstr(result, bodyReg));
            EndBlock(new BrTerm(endLbl));

            StartBlock(nextLbl);
        }

        if (!_blockEnded) EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }
}
