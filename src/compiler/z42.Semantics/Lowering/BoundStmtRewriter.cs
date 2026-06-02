using Z42.Semantics.Bound;

namespace Z42.Semantics.Lowering;

/// <summary>
/// Tree-to-tree rewriter over the <see cref="BoundStmt"/> hierarchy. Same
/// identity-short-circuit semantics as <see cref="BoundExprRewriter"/>.
///
/// Statement nodes contain both <see cref="BoundStmt"/> children (recursed
/// via this visitor) and <see cref="BoundExpr"/> children (delegated to
/// the overridable <see cref="RewriteExpr"/> hook — default identity). A
/// lowering pass that needs to transform both expressions and statements
/// composes this with a <see cref="BoundExprRewriter"/> by overriding
/// <see cref="RewriteExpr"/> to forward to the expression rewriter:
///
/// <code>
/// class MyPass : BoundStmtRewriter {
///     private readonly BoundExprRewriter _exprPass = new MyExprPass();
///     protected override BoundExpr RewriteExpr(BoundExpr e) =&gt; _exprPass.Visit(e);
/// }
/// </code>
///
/// review.md F2.5 Phase 1 (2026-06-02).
/// </summary>
public abstract class BoundStmtRewriter : BoundStmtVisitor<BoundStmt>
{
    // ── Expression-rewrite hook ──────────────────────────────────────────────
    //
    // Statements carry BoundExpr children (e.g. `BoundIf.Cond`, `BoundReturn.Value`,
    // `BoundForeach.Collection`). Default = identity; subclasses that also want
    // to rewrite expressions override this and forward to a BoundExprRewriter.

    /// <summary>
    /// Rewrite a BoundExpr child of a BoundStmt. Default = identity. Override
    /// to compose with a <see cref="BoundExprRewriter"/>.
    /// </summary>
    protected virtual BoundExpr RewriteExpr(BoundExpr e) => e;

    private BoundExpr? RewriteExprIfPresent(BoundExpr? e) =>
        e == null ? null : RewriteExpr(e);

    // ── Block helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Rewrite every statement inside a block. Returns the input block
    /// reference when nothing changed (identity short-circuit), else a
    /// fresh <see cref="BoundBlock"/> with the rewritten statement list.
    /// </summary>
    public BoundBlock RewriteBlock(BoundBlock block)
    {
        var newStmts = BoundExprRewriter.RewriteList(block.Stmts, Visit);
        return ReferenceEquals(newStmts, block.Stmts) ? block : block with { Stmts = newStmts };
    }

    private BoundBlock? RewriteBlockIfPresent(BoundBlock? block) =>
        block == null ? null : RewriteBlock(block);

    // ── Leaves ───────────────────────────────────────────────────────────────

    protected override BoundStmt VisitBreak(BoundBreak br)       => br;
    protected override BoundStmt VisitContinue(BoundContinue co) => co;

    // ── Block as statement ───────────────────────────────────────────────────

    protected override BoundStmt VisitBlockStmt(BoundBlockStmt b)
    {
        var newBlock = RewriteBlock(b.Block);
        return ReferenceEquals(newBlock, b.Block) ? b : b with { Block = newBlock };
    }

    // ── Variable / return / expression statements ────────────────────────────

    protected override BoundStmt VisitVarDecl(BoundVarDecl v)
    {
        var init = RewriteExprIfPresent(v.Init);
        return ReferenceEquals(init, v.Init) ? v : v with { Init = init };
    }

    protected override BoundStmt VisitReturn(BoundReturn r)
    {
        var value = RewriteExprIfPresent(r.Value);
        return ReferenceEquals(value, r.Value) ? r : r with { Value = value };
    }

    protected override BoundStmt VisitExprStmt(BoundExprStmt e)
    {
        var expr = RewriteExpr(e.Expr);
        return ReferenceEquals(expr, e.Expr) ? e : e with { Expr = expr };
    }

    protected override BoundStmt VisitThrow(BoundThrow th)
    {
        var value = RewriteExpr(th.Value);
        return ReferenceEquals(value, th.Value) ? th : th with { Value = value };
    }

    // ── Control flow ─────────────────────────────────────────────────────────

    protected override BoundStmt VisitIf(BoundIf i)
    {
        var cond  = RewriteExpr(i.Cond);
        var then_ = RewriteBlock(i.Then);
        var else_ = i.Else != null ? Visit(i.Else) : null;
        return ReferenceEquals(cond, i.Cond)
            && ReferenceEquals(then_, i.Then)
            && ReferenceEquals(else_, i.Else)
            ? i : i with { Cond = cond, Then = then_, Else = else_ };
    }

    protected override BoundStmt VisitWhile(BoundWhile w)
    {
        var cond = RewriteExpr(w.Cond);
        var body = RewriteBlock(w.Body);
        return ReferenceEquals(cond, w.Cond) && ReferenceEquals(body, w.Body)
            ? w : w with { Cond = cond, Body = body };
    }

    protected override BoundStmt VisitDoWhile(BoundDoWhile dw)
    {
        var body = RewriteBlock(dw.Body);
        var cond = RewriteExpr(dw.Cond);
        return ReferenceEquals(body, dw.Body) && ReferenceEquals(cond, dw.Cond)
            ? dw : dw with { Body = body, Cond = cond };
    }

    protected override BoundStmt VisitFor(BoundFor f)
    {
        var init = f.Init != null ? Visit(f.Init) : null;
        var cond = RewriteExprIfPresent(f.Cond);
        var incr = RewriteExprIfPresent(f.Increment);
        var body = RewriteBlock(f.Body);
        return ReferenceEquals(init, f.Init)
            && ReferenceEquals(cond, f.Cond)
            && ReferenceEquals(incr, f.Increment)
            && ReferenceEquals(body, f.Body)
            ? f : f with { Init = init, Cond = cond, Increment = incr, Body = body };
    }

    protected override BoundStmt VisitForeach(BoundForeach fe)
    {
        var coll = RewriteExpr(fe.Collection);
        var body = RewriteBlock(fe.Body);
        return ReferenceEquals(coll, fe.Collection) && ReferenceEquals(body, fe.Body)
            ? fe : fe with { Collection = coll, Body = body };
    }

    // ── Switch ───────────────────────────────────────────────────────────────

    protected override BoundStmt VisitSwitch(BoundSwitch sw)
    {
        var subject = RewriteExpr(sw.Subject);
        var newCases = BoundExprRewriter.RewriteList(sw.Cases, RewriteSwitchCase);
        return ReferenceEquals(subject, sw.Subject) && ReferenceEquals(newCases, sw.Cases)
            ? sw : sw with { Subject = subject, Cases = newCases };
    }

    private BoundSwitchCase RewriteSwitchCase(BoundSwitchCase c)
    {
        var pattern = c.Pattern != null ? RewriteExpr(c.Pattern) : null;
        var body    = BoundExprRewriter.RewriteList(c.Body, Visit);
        return ReferenceEquals(pattern, c.Pattern) && ReferenceEquals(body, c.Body)
            ? c : c with { Pattern = pattern, Body = body };
    }

    // ── Try / catch ──────────────────────────────────────────────────────────

    protected override BoundStmt VisitTryCatch(BoundTryCatch tc)
    {
        var tryBody  = RewriteBlock(tc.TryBody);
        var catches  = BoundExprRewriter.RewriteList(tc.Catches, RewriteCatchClause);
        var finally_ = RewriteBlockIfPresent(tc.Finally);
        return ReferenceEquals(tryBody, tc.TryBody)
            && ReferenceEquals(catches, tc.Catches)
            && ReferenceEquals(finally_, tc.Finally)
            ? tc : tc with { TryBody = tryBody, Catches = catches, Finally = finally_ };
    }

    private BoundCatchClause RewriteCatchClause(BoundCatchClause c)
    {
        var body = RewriteBlock(c.Body);
        return ReferenceEquals(body, c.Body) ? c : c with { Body = body };
    }

    // ── Local function ───────────────────────────────────────────────────────

    protected override BoundStmt VisitLocalFunction(BoundLocalFunction lf)
    {
        var body = RewriteBlock(lf.Body);
        return ReferenceEquals(body, lf.Body) ? lf : lf with { Body = body };
    }

    // ── Pinned ───────────────────────────────────────────────────────────────

    protected override BoundStmt VisitPinned(BoundPinned p)
    {
        var src  = RewriteExpr(p.Source);
        var body = RewriteBlock(p.Body);
        return ReferenceEquals(src, p.Source) && ReferenceEquals(body, p.Body)
            ? p : p with { Source = src, Body = body };
    }
}
