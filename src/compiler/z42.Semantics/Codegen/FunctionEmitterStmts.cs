using Z42.Semantics.Bound;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Bound statement and control flow emission — part of FunctionEmitter.
internal sealed partial class FunctionEmitter
{
    // ── Block ─────────────────────────────────────────────────────────────────

    private void EmitBoundBlock(BoundBlock block)
    {
        foreach (var stmt in block.Stmts)
        {
            if (_blockEnded) break;
            EmitBoundStmt(stmt);
        }
    }

    // ── Statements ────────────────────────────────────────────────────────────

    private void EmitBoundStmt(BoundStmt stmt)
    {
        switch (stmt)
        {
            case BoundVarDecl v:
                _mutableVars.Add(v.Name);
                if (v.Init != null)
                {
                    int reg = EmitExpr(v.Init);
                    Emit(new StoreInstr(v.Name, reg));
                }
                break;

            case BoundReturn r:
                EndBlock(r.Value != null
                    ? new RetTerm(EmitExpr(r.Value))
                    : new RetTerm(null));
                break;

            case BoundExprStmt e:
                EmitExpr(e.Expr);
                break;

            case BoundBlockStmt b:
                EmitBoundBlock(b.Block);
                break;

            case BoundIf ifStmt:
                EmitBoundIf(ifStmt);
                break;

            case BoundWhile ws:
                EmitBoundWhile(ws);
                break;

            case BoundDoWhile dw:
                EmitBoundDoWhile(dw);
                break;

            case BoundFor fs:
                EmitBoundFor(fs);
                break;

            case BoundForeach fe:
                EmitBoundForeach(fe);
                break;

            case BoundBreak:
                if (_loopStack.Count > 0)
                    EndBlock(new BrTerm(_loopStack.Peek().Break));
                break;

            case BoundContinue:
                if (_loopStack.Count > 0)
                    EndBlock(new BrTerm(_loopStack.Peek().Continue));
                break;

            case BoundSwitch sw:
                EmitBoundSwitchStmt(sw);
                break;

            case BoundTryCatch tc:
                EmitBoundTryCatch(tc);
                break;

            case BoundThrow th:
                EndBlock(new ThrowTerm(EmitExpr(th.Value)));
                break;
        }
    }

    // ── Control flow ──────────────────────────────────────────────────────────

    private void EmitBoundIf(BoundIf ifStmt)
    {
        int condReg    = EmitExpr(ifStmt.Cond);
        string thenLbl = FreshLabel("then");
        string elseLbl = FreshLabel(ifStmt.Else != null ? "else" : "end");
        string endLbl  = ifStmt.Else != null ? FreshLabel("end") : elseLbl;

        EndBlock(new BrCondTerm(condReg, thenLbl, elseLbl));

        StartBlock(thenLbl);
        EmitBoundBlock(ifStmt.Then);
        if (!_blockEnded) EndBlock(new BrTerm(endLbl));

        if (ifStmt.Else != null)
        {
            StartBlock(elseLbl);
            EmitBoundStmt(ifStmt.Else);
            if (!_blockEnded) EndBlock(new BrTerm(endLbl));
        }

        StartBlock(endLbl);
    }

    private void EmitBoundWhile(BoundWhile ws)
    {
        string condLbl = FreshLabel("cond");
        string bodyLbl = FreshLabel("body");
        string endLbl  = FreshLabel("end");

        EndBlock(new BrTerm(condLbl));

        StartBlock(condLbl);
        int condReg = EmitExpr(ws.Cond);
        EndBlock(new BrCondTerm(condReg, bodyLbl, endLbl));

        _loopStack.Push((endLbl, condLbl));
        StartBlock(bodyLbl);
        EmitBoundBlock(ws.Body);
        if (!_blockEnded) EndBlock(new BrTerm(condLbl));
        _loopStack.Pop();

        StartBlock(endLbl);
    }

    private void EmitBoundDoWhile(BoundDoWhile dw)
    {
        string bodyLbl = FreshLabel("do_body");
        string condLbl = FreshLabel("do_cond");
        string endLbl  = FreshLabel("do_end");

        EndBlock(new BrTerm(bodyLbl));

        _loopStack.Push((endLbl, condLbl));
        StartBlock(bodyLbl);
        EmitBoundBlock(dw.Body);
        if (!_blockEnded) EndBlock(new BrTerm(condLbl));
        _loopStack.Pop();

        StartBlock(condLbl);
        int condReg = EmitExpr(dw.Cond);
        EndBlock(new BrCondTerm(condReg, bodyLbl, endLbl));

        StartBlock(endLbl);
    }

    private void EmitBoundFor(BoundFor fs)
    {
        if (fs.Init != null) EmitBoundStmt(fs.Init);

        string condLbl = FreshLabel("cond");
        string bodyLbl = FreshLabel("body");
        string incrLbl = FreshLabel("incr");
        string endLbl  = FreshLabel("end");

        EndBlock(new BrTerm(condLbl));

        StartBlock(condLbl);
        if (fs.Cond != null)
        {
            int condReg = EmitExpr(fs.Cond);
            EndBlock(new BrCondTerm(condReg, bodyLbl, endLbl));
        }
        else
        {
            EndBlock(new BrTerm(bodyLbl));
        }

        _loopStack.Push((endLbl, incrLbl));
        StartBlock(bodyLbl);
        EmitBoundBlock(fs.Body);
        if (!_blockEnded) EndBlock(new BrTerm(incrLbl));
        _loopStack.Pop();

        StartBlock(incrLbl);
        if (fs.Increment != null) EmitExpr(fs.Increment);
        EndBlock(new BrTerm(condLbl));

        StartBlock(endLbl);
    }

    private void EmitBoundForeach(BoundForeach fe)
    {
        int arrReg = EmitExpr(fe.Collection);
        int lenReg = Alloc();
        Emit(new ArrayLenInstr(lenReg, arrReg));

        string indexVar = $"__fe_i_{_nextLabelId}";
        _mutableVars.Add(indexVar);
        int zeroReg = Alloc();
        Emit(new ConstI32Instr(zeroReg, 0));
        Emit(new StoreInstr(indexVar, zeroReg));

        string condLbl = FreshLabel("fe_cond");
        string bodyLbl = FreshLabel("fe_body");
        string incrLbl = FreshLabel("fe_inc");
        string endLbl  = FreshLabel("fe_end");

        EndBlock(new BrTerm(condLbl));

        StartBlock(condLbl);
        int iReg   = Alloc();
        Emit(new LoadInstr(iReg, indexVar));
        int cmpReg = Alloc();
        Emit(new LtInstr(cmpReg, iReg, lenReg));
        EndBlock(new BrCondTerm(cmpReg, bodyLbl, endLbl));

        _loopStack.Push((endLbl, incrLbl));
        StartBlock(bodyLbl);
        int iReg2   = Alloc();
        Emit(new LoadInstr(iReg2, indexVar));
        int elemReg = Alloc();
        Emit(new ArrayGetInstr(elemReg, arrReg, iReg2));
        _mutableVars.Add(fe.VarName);
        Emit(new StoreInstr(fe.VarName, elemReg));
        EmitBoundBlock(fe.Body);
        if (!_blockEnded) EndBlock(new BrTerm(incrLbl));
        _loopStack.Pop();

        StartBlock(incrLbl);
        int iReg3   = Alloc();
        Emit(new LoadInstr(iReg3, indexVar));
        int oneReg  = Alloc();
        Emit(new ConstI32Instr(oneReg, 1));
        int nextReg = Alloc();
        Emit(new AddInstr(nextReg, iReg3, oneReg));
        Emit(new StoreInstr(indexVar, nextReg));
        EndBlock(new BrTerm(condLbl));

        StartBlock(endLbl);
    }

    // ── Switch statement ──────────────────────────────────────────────────────

    private void EmitBoundSwitchStmt(BoundSwitch sw)
    {
        int subjReg = EmitExpr(sw.Subject);
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

            int patReg = EmitExpr(c.Pattern);
            int cmpReg = Alloc();
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

        foreach (var clause in tc.Catches)
        {
            int catchReg         = Alloc();
            string catchStartLbl = FreshLabel("catch_start");

            // ExceptionType is not stored in BoundCatchClause — use wildcard catch
            _exceptionTable.Add(new IrExceptionEntry(
                tryStartLbl, tryEndLbl, catchStartLbl, null, catchReg));

            StartBlock(catchStartLbl);
            if (clause.VarName != null)
            {
                _mutableVars.Add(clause.VarName);
                Emit(new StoreInstr(clause.VarName, catchReg));
            }
            EmitBoundBlock(clause.Body);
            if (!_blockEnded) EndBlock(new BrTerm(finallyLbl));
        }

        if (tc.Catches.Count == 0 && tc.Finally != null)
        {
            int catchAllReg         = Alloc();
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

        if (tc.Finally != null)
        {
            StartBlock(finallyLbl);
            EmitBoundBlock(tc.Finally);
            if (!_blockEnded) EndBlock(new BrTerm(afterLbl));
        }

        StartBlock(afterLbl);
    }
}
