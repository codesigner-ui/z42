using Z42.Core.Text;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Bound;

// ── Bound statement hierarchy ─────────────────────────────────────────────────
// BoundBlock is the primary unit produced by TypeChecker and consumed by FunctionEmitter.

/// A list of bound statements with their source span.
/// Used as function bodies, if-branches, loop bodies, etc.
public sealed record BoundBlock(IReadOnlyList<BoundStmt> Stmts, Span Span);

public abstract record BoundStmt(Span Span)
{
    public abstract TResult Accept<TResult>(IBoundStmtVisitor<TResult> visitor);
}

// ── Block as statement ────────────────────────────────────────────────────────

public sealed record BoundBlockStmt(BoundBlock Block, Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitBlock(this); }

// ── Variable declaration ──────────────────────────────────────────────────────

/// Bound variable declaration. VarType is already resolved; never Z42Type.Unknown
/// unless the variable has no annotation and no initializer.
public sealed record BoundVarDecl(
    string Name,
    Z42Type VarType,
    BoundExpr? Init,
    Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitVarDecl(this); }

// ── Return ────────────────────────────────────────────────────────────────────

public sealed record BoundReturn(BoundExpr? Value, Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitReturn(this); }

// ── Expression statement ─────────────────────────────────────────────���────────

public sealed record BoundExprStmt(BoundExpr Expr, Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitExprStmt(this); }

// ── Control flow ──────────────────────────────────────────────────────────────

public sealed record BoundIf(
    BoundExpr Cond,
    BoundBlock Then,
    BoundStmt? Else,
    Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitIf(this); }

public sealed record BoundWhile(BoundExpr Cond, BoundBlock Body, Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitWhile(this); }

public sealed record BoundDoWhile(BoundBlock Body, BoundExpr Cond, Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitDoWhile(this); }

public sealed record BoundFor(
    BoundStmt? Init,
    BoundExpr? Cond,
    BoundExpr? Increment,
    BoundBlock Body,
    Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitFor(this); }

public sealed record BoundForeach(
    string VarName,
    Z42Type VarType,
    BoundExpr Collection,
    BoundBlock Body,
    Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitForeach(this); }

public sealed record BoundBreak(Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitBreak(this); }

public sealed record BoundContinue(Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitContinue(this); }

// ── Switch ────────────────────────────────────────────────────────────────────

public sealed record BoundSwitch(
    BoundExpr Subject,
    IReadOnlyList<BoundSwitchCase> Cases,
    Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitSwitch(this); }

public sealed record BoundSwitchCase(
    BoundExpr? Pattern,
    IReadOnlyList<BoundStmt> Body,
    Span Span);

// ── Try / catch ───────────────────────────────────────────────────────────────

public sealed record BoundTryCatch(
    BoundBlock TryBody,
    IReadOnlyList<BoundCatchClause> Catches,
    BoundBlock? Finally,
    Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitTryCatch(this); }

public sealed record BoundCatchClause(string? VarName, BoundBlock Body, Span Span);

// ── Throw ─────────────────────────────────────────────────────────────────────

public sealed record BoundThrow(BoundExpr Value, Span Span) : BoundStmt(Span)
{ public override TResult Accept<TResult>(IBoundStmtVisitor<TResult> v) => v.VisitThrow(this); }
