namespace Z42.Semantics.Bound;

/// Type-driven dispatcher over the BoundStmt hierarchy. See BoundExprVisitor
/// for the rationale and "add a new node" workflow.
public abstract class BoundStmtVisitor<TResult>
{
    public TResult Visit(BoundStmt s) => s switch
    {
        BoundBlockStmt b      => VisitBlockStmt(b),
        BoundVarDecl v        => VisitVarDecl(v),
        BoundReturn r         => VisitReturn(r),
        BoundExprStmt e       => VisitExprStmt(e),
        BoundIf i             => VisitIf(i),
        BoundWhile w          => VisitWhile(w),
        BoundDoWhile dw       => VisitDoWhile(dw),
        BoundFor f            => VisitFor(f),
        BoundForeach fe       => VisitForeach(fe),
        BoundBreak br         => VisitBreak(br),
        BoundContinue co      => VisitContinue(co),
        BoundSwitch sw        => VisitSwitch(sw),
        BoundTryCatch tc      => VisitTryCatch(tc),
        BoundThrow th         => VisitThrow(th),
        BoundLocalFunction lf => VisitLocalFunction(lf),
        BoundPinned p         => VisitPinned(p),
        _ => throw new InvalidOperationException(
            $"BoundStmtVisitor: unhandled BoundStmt subtype `{s.GetType().Name}` (ICE — add a case to the base switch)")
    };

    protected abstract TResult VisitBlockStmt(BoundBlockStmt b);
    protected abstract TResult VisitVarDecl(BoundVarDecl v);
    protected abstract TResult VisitReturn(BoundReturn r);
    protected abstract TResult VisitExprStmt(BoundExprStmt e);
    protected abstract TResult VisitIf(BoundIf i);
    protected abstract TResult VisitWhile(BoundWhile w);
    protected abstract TResult VisitDoWhile(BoundDoWhile dw);
    protected abstract TResult VisitFor(BoundFor f);
    protected abstract TResult VisitForeach(BoundForeach fe);
    protected abstract TResult VisitBreak(BoundBreak br);
    protected abstract TResult VisitContinue(BoundContinue co);
    protected abstract TResult VisitSwitch(BoundSwitch sw);
    protected abstract TResult VisitTryCatch(BoundTryCatch tc);
    protected abstract TResult VisitThrow(BoundThrow th);
    protected abstract TResult VisitLocalFunction(BoundLocalFunction lf);
    protected abstract TResult VisitPinned(BoundPinned p);
}

/// Default void-walker: leaves are no-ops, interior nodes recurse.
/// Stmt walkers do NOT recurse into expression children by default — subclasses
/// that need expression traversal compose with a BoundExprVisitor / Walker.
public abstract class BoundStmtWalker : BoundStmtVisitor<Unit>
{
    /// Walk every statement inside a block. Useful entry point for whole-body scans.
    public void WalkBlock(BoundBlock block)
    {
        foreach (var s in block.Stmts) Visit(s);
    }

    // ── Leaves ────────────────────────────────────────────────────────────────
    protected override Unit VisitBreak(BoundBreak br)       => default;
    protected override Unit VisitContinue(BoundContinue co) => default;

    // ── Stmts that hold only expressions (no nested stmts) ────────────────────
    protected override Unit VisitVarDecl(BoundVarDecl v)
    {
        VisitExprIfPresent(v.Init);
        return default;
    }

    protected override Unit VisitReturn(BoundReturn r)
    {
        VisitExprIfPresent(r.Value);
        return default;
    }

    protected override Unit VisitExprStmt(BoundExprStmt e)
    {
        VisitExpr(e.Expr);
        return default;
    }

    protected override Unit VisitThrow(BoundThrow th)
    {
        VisitExpr(th.Value);
        return default;
    }

    // ── Stmts that nest blocks / stmts ────────────────────────────────────────
    protected override Unit VisitBlockStmt(BoundBlockStmt b)
    {
        WalkBlock(b.Block);
        return default;
    }

    protected override Unit VisitIf(BoundIf i)
    {
        VisitExpr(i.Cond);
        WalkBlock(i.Then);
        if (i.Else != null) Visit(i.Else);
        return default;
    }

    protected override Unit VisitWhile(BoundWhile w)
    {
        VisitExpr(w.Cond);
        WalkBlock(w.Body);
        return default;
    }

    protected override Unit VisitDoWhile(BoundDoWhile dw)
    {
        WalkBlock(dw.Body);
        VisitExpr(dw.Cond);
        return default;
    }

    protected override Unit VisitFor(BoundFor f)
    {
        if (f.Init != null) Visit(f.Init);
        VisitExprIfPresent(f.Cond);
        VisitExprIfPresent(f.Increment);
        WalkBlock(f.Body);
        return default;
    }

    protected override Unit VisitForeach(BoundForeach fe)
    {
        VisitExpr(fe.Collection);
        WalkBlock(fe.Body);
        return default;
    }

    protected override Unit VisitSwitch(BoundSwitch sw)
    {
        VisitExpr(sw.Subject);
        foreach (var c in sw.Cases)
        {
            if (c.Pattern != null) VisitExpr(c.Pattern);
            foreach (var s in c.Body) Visit(s);
        }
        return default;
    }

    protected override Unit VisitTryCatch(BoundTryCatch tc)
    {
        WalkBlock(tc.TryBody);
        foreach (var clause in tc.Catches) WalkBlock(clause.Body);
        if (tc.Finally != null) WalkBlock(tc.Finally);
        return default;
    }

    protected override Unit VisitLocalFunction(BoundLocalFunction lf)
    {
        WalkBlock(lf.Body);
        return default;
    }

    protected override Unit VisitPinned(BoundPinned p)
    {
        VisitExpr(p.Source);
        WalkBlock(p.Body);
        return default;
    }

    /// Hook for subclasses to traverse a BoundExpr child. Default no-op so
    /// stmt-only walkers don't need to instantiate an expression visitor.
    /// Subclasses that need expression traversal override and forward to
    /// their composed BoundExprVisitor / Walker.
    protected virtual void VisitExpr(BoundExpr e) { }

    private void VisitExprIfPresent(BoundExpr? e)
    {
        if (e != null) VisitExpr(e);
    }
}
