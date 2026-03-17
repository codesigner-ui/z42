using Z42.Compiler.Parser;
using Z42.IR;

namespace Z42.Compiler.Codegen;

/// Statement and control flow emission — part of the IrGen partial class.
public sealed partial class IrGen
{
    // ── Statements ────────────────────────────────────────────────────────────

    private void EmitBlock(BlockStmt block)
    {
        foreach (var stmt in block.Stmts)
        {
            if (_blockEnded) break;   // dead code after return/break/continue
            EmitStmt(stmt);
        }
    }

    private void EmitStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case VarDeclStmt v:
                _mutableVars.Add(v.Name);   // mark as mutable local
                if (v.Init != null)
                {
                    int reg = EmitExpr(v.Init);
                    Emit(new StoreInstr(v.Name, reg));
                }
                break;

            case ReturnStmt r:
                EndBlock(r.Value != null
                    ? new RetTerm(EmitExpr(r.Value))
                    : new RetTerm(null));
                break;

            case ExprStmt e:
                EmitExpr(e.Expr);
                break;

            case BlockStmt b:
                EmitBlock(b);
                break;

            case IfStmt ifStmt:
                EmitIf(ifStmt);
                break;

            case WhileStmt ws:
                EmitWhile(ws);
                break;

            case ForStmt fs:
                EmitFor(fs);
                break;

            case ForeachStmt fe:
                EmitForeach(fe);
                break;

            case BreakStmt:
                if (_loopStack.Count > 0)
                    EndBlock(new BrTerm(_loopStack.Peek().Break));
                break;

            case ContinueStmt:
                if (_loopStack.Count > 0)
                    EndBlock(new BrTerm(_loopStack.Peek().Continue));
                break;
        }
    }

    // ── Control flow ──────────────────────────────────────────────────────────

    private void EmitIf(IfStmt ifStmt)
    {
        int condReg    = EmitExpr(ifStmt.Condition);
        string thenLbl = FreshLabel("then");
        string elseLbl = FreshLabel(ifStmt.Else != null ? "else" : "end");
        string endLbl  = ifStmt.Else != null ? FreshLabel("end") : elseLbl;

        EndBlock(new BrCondTerm(condReg, thenLbl, elseLbl));

        // then branch
        StartBlock(thenLbl);
        EmitBlock(ifStmt.Then);
        if (!_blockEnded) EndBlock(new BrTerm(endLbl));

        // else branch (optional)
        if (ifStmt.Else != null)
        {
            StartBlock(elseLbl);
            EmitStmt(ifStmt.Else);
            if (!_blockEnded) EndBlock(new BrTerm(endLbl));
        }

        // merge / continuation
        StartBlock(endLbl);
    }

    private void EmitWhile(WhileStmt ws)
    {
        string condLbl = FreshLabel("cond");
        string bodyLbl = FreshLabel("body");
        string endLbl  = FreshLabel("end");

        EndBlock(new BrTerm(condLbl));

        StartBlock(condLbl);
        int condReg = EmitExpr(ws.Condition);
        EndBlock(new BrCondTerm(condReg, bodyLbl, endLbl));

        // continue → re-check condition; break → exit
        _loopStack.Push((endLbl, condLbl));
        StartBlock(bodyLbl);
        EmitBlock(ws.Body);
        if (!_blockEnded) EndBlock(new BrTerm(condLbl));
        _loopStack.Pop();

        StartBlock(endLbl);
    }

    private void EmitFor(ForStmt fs)
    {
        if (fs.Init != null) EmitStmt(fs.Init);

        string condLbl = FreshLabel("cond");
        string bodyLbl = FreshLabel("body");
        string incrLbl = FreshLabel("incr");
        string endLbl  = FreshLabel("end");

        EndBlock(new BrTerm(condLbl));

        StartBlock(condLbl);
        if (fs.Condition != null)
        {
            int condReg = EmitExpr(fs.Condition);
            EndBlock(new BrCondTerm(condReg, bodyLbl, endLbl));
        }
        else
        {
            EndBlock(new BrTerm(bodyLbl));  // infinite loop
        }

        // continue → increment; break → exit
        _loopStack.Push((endLbl, incrLbl));
        StartBlock(bodyLbl);
        EmitBlock(fs.Body);
        if (!_blockEnded) EndBlock(new BrTerm(incrLbl));
        _loopStack.Pop();

        StartBlock(incrLbl);
        if (fs.Increment != null) EmitExpr(fs.Increment);
        EndBlock(new BrTerm(condLbl));

        StartBlock(endLbl);
    }

    private void EmitForeach(ForeachStmt fe)
    {
        // Evaluate collection once in the current (pre-loop) block
        int arrReg = EmitExpr(fe.Collection);

        int lenReg = Alloc();
        Emit(new ArrayLenInstr(lenReg, arrReg));

        // Init counter variable
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

        // Condition: i < len
        StartBlock(condLbl);
        int iReg   = Alloc();
        Emit(new LoadInstr(iReg, indexVar));
        int cmpReg = Alloc();
        Emit(new LtInstr(cmpReg, iReg, lenReg));
        EndBlock(new BrCondTerm(cmpReg, bodyLbl, endLbl));

        // Body: load element, bind loop variable, run body
        // continue → increment block; break → end
        _loopStack.Push((endLbl, incrLbl));
        StartBlock(bodyLbl);
        int iReg2   = Alloc();
        Emit(new LoadInstr(iReg2, indexVar));
        int elemReg = Alloc();
        Emit(new ArrayGetInstr(elemReg, arrReg, iReg2));
        _mutableVars.Add(fe.VarName);
        Emit(new StoreInstr(fe.VarName, elemReg));
        EmitBlock(fe.Body);
        if (!_blockEnded) EndBlock(new BrTerm(incrLbl));
        _loopStack.Pop();

        // Increment: i = i + 1
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
}
