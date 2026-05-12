using Z42.Semantics.Bound;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Switch-statement + try/catch emission. spec split-emitter-stmts-calls
/// (2026-05-12): extracted from FunctionEmitterStmts.cs to keep the main
/// file under the 300 LOC soft limit. Zero behavior change.
internal sealed partial class FunctionEmitter
{
    // ── Switch statement ──────────────────────────────────────────────────────

    private void EmitBoundSwitchStmt(BoundSwitch sw)
    {
        var subjReg = EmitExpr(sw.Subject);
        string endLbl = FreshLabel("sw_end");

        string outerContinue = _loopStack.Count > 0 ? _loopStack.Peek().Continue : endLbl;
        _loopStack.Push((endLbl, outerContinue));

        foreach (var c in sw.Cases)
        {
            if (c.Pattern == null)
            {
                foreach (var s in c.Body)
                {
                    if (_blockEnded) break;
                    EmitBoundStmt(s);
                }
                if (!_blockEnded) EndBlock(new BrTerm(endLbl));
                break;
            }

            string bodyLbl = FreshLabel("sw_case");
            string nextLbl = FreshLabel("sw_next");

            var patReg = EmitExpr(c.Pattern);
            var cmpReg = Alloc(IrType.Bool);
            Emit(new EqInstr(cmpReg, subjReg, patReg));
            EndBlock(new BrCondTerm(cmpReg, bodyLbl, nextLbl));

            StartBlock(bodyLbl);
            foreach (var s in c.Body)
            {
                if (_blockEnded) break;
                EmitBoundStmt(s);
            }
            if (!_blockEnded) EndBlock(new BrTerm(endLbl));

            StartBlock(nextLbl);
        }

        _loopStack.Pop();

        if (!_blockEnded) EndBlock(new BrTerm(endLbl));
        StartBlock(endLbl);
    }

    // ── Try / catch ───────────────────────────────────────────────────────────

    private void EmitBoundTryCatch(BoundTryCatch tc)
    {
        string tryStartLbl = FreshLabel("try_start");
        string tryEndLbl   = FreshLabel("try_end");
        string afterLbl    = FreshLabel("after_try");
        string finallyLbl  = tc.Finally != null ? FreshLabel("finally") : afterLbl;

        EndBlock(new BrTerm(tryStartLbl));

        StartBlock(tryStartLbl);
        EmitBoundBlock(tc.TryBody);
        if (!_blockEnded) EndBlock(new BrTerm(tryEndLbl));

        StartBlock(tryEndLbl);
        EndBlock(new BrTerm(finallyLbl));

        EmitTryCatchClauses(tc, tryStartLbl, tryEndLbl, finallyLbl);

        if (tc.Finally != null)
        {
            StartBlock(finallyLbl);
            EmitBoundBlock(tc.Finally);
            if (!_blockEnded) EndBlock(new BrTerm(afterLbl));
        }

        StartBlock(afterLbl);
    }

    /// Catch-clause + (when no user catches and a finally is present)
    /// synthetic catch-all that runs the finally body and rethrows.
    private void EmitTryCatchClauses(
        BoundTryCatch tc, string tryStartLbl, string tryEndLbl, string finallyLbl)
    {
        foreach (var clause in tc.Catches)
        {
            var catchReg         = Alloc(IrType.Ref);
            string catchStartLbl = FreshLabel("catch_start");

            // catch-by-generic-type (2026-05-06): emit BoundCatchClause's resolved
            // exception type FQ name so VM `find_handler` can filter by class.
            // null = untyped catch (`catch { }` / `catch (e)`) → wildcard;
            // non-null = typed catch — VM matches via instance-of with subclass walk.
            _exceptionTable.Add(new IrExceptionEntry(
                tryStartLbl, tryEndLbl, catchStartLbl, clause.ExceptionTypeName, catchReg));

            StartBlock(catchStartLbl);
            if (clause.VarName != null)
            {
                // Exception variable binding (now pure register-based)
                WriteBackName(clause.VarName, catchReg);
            }
            EmitBoundBlock(clause.Body);
            if (!_blockEnded) EndBlock(new BrTerm(finallyLbl));
        }

        if (tc.Catches.Count == 0 && tc.Finally != null)
        {
            var catchAllReg         = Alloc(IrType.Ref);
            string catchAllStartLbl = FreshLabel("catch_finally");
            string rethrowLbl       = FreshLabel("rethrow");

            _exceptionTable.Add(new IrExceptionEntry(
                tryStartLbl, tryEndLbl, catchAllStartLbl, "*", catchAllReg));

            StartBlock(catchAllStartLbl);
            EmitBoundBlock(tc.Finally);
            if (!_blockEnded) EndBlock(new BrTerm(rethrowLbl));

            StartBlock(rethrowLbl);
            EndBlock(new ThrowTerm(catchAllReg));
        }
    }
}
