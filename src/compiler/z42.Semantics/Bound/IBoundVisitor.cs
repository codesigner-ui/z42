namespace Z42.Semantics.Bound;

// ── Visitor interfaces ───────────────────────────────────────────────────────
// Adding a new BoundExpr / BoundStmt sealed record forces implementors to
// add the corresponding Visit method — the compiler catches the omission.

/// <summary>Visitor over the <see cref="BoundExpr"/> hierarchy.</summary>
public interface IBoundExprVisitor<out TResult>
{
    // Literals
    TResult VisitLitInt(BoundLitInt node);
    TResult VisitLitFloat(BoundLitFloat node);
    TResult VisitLitStr(BoundLitStr node);
    TResult VisitLitBool(BoundLitBool node);
    TResult VisitLitNull(BoundLitNull node);
    TResult VisitLitChar(BoundLitChar node);
    TResult VisitInterpolatedStr(BoundInterpolatedStr node);

    // Core
    TResult VisitIdent(BoundIdent node);
    TResult VisitAssign(BoundAssign node);
    TResult VisitBinary(BoundBinary node);
    TResult VisitUnary(BoundUnary node);
    TResult VisitPostfix(BoundPostfix node);

    // Calls
    TResult VisitCall(BoundCall node);

    // Member / index
    TResult VisitMember(BoundMember node);
    TResult VisitIndex(BoundIndex node);

    // Type operations
    TResult VisitCast(BoundCast node);
    TResult VisitNew(BoundNew node);
    TResult VisitArrayCreate(BoundArrayCreate node);
    TResult VisitArrayLit(BoundArrayLit node);

    // Control-flow expressions
    TResult VisitConditional(BoundConditional node);
    TResult VisitNullCoalesce(BoundNullCoalesce node);
    TResult VisitNullConditional(BoundNullConditional node);
    TResult VisitIsPattern(BoundIsPattern node);
    TResult VisitSwitchExpr(BoundSwitchExpr node);

    // Error sentinel
    TResult VisitError(BoundError node);
}

/// <summary>Visitor over the <see cref="BoundStmt"/> hierarchy.</summary>
public interface IBoundStmtVisitor<out TResult>
{
    TResult VisitBlock(BoundBlockStmt node);
    TResult VisitVarDecl(BoundVarDecl node);
    TResult VisitReturn(BoundReturn node);
    TResult VisitExprStmt(BoundExprStmt node);
    TResult VisitIf(BoundIf node);
    TResult VisitWhile(BoundWhile node);
    TResult VisitDoWhile(BoundDoWhile node);
    TResult VisitFor(BoundFor node);
    TResult VisitForeach(BoundForeach node);
    TResult VisitBreak(BoundBreak node);
    TResult VisitContinue(BoundContinue node);
    TResult VisitSwitch(BoundSwitch node);
    TResult VisitTryCatch(BoundTryCatch node);
    TResult VisitThrow(BoundThrow node);
}
