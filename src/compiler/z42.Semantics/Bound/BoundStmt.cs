using Z42.Core.Text;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Bound;

// ── Bound statement hierarchy ─────────────────────────────────────────────────
// BoundBlock is the primary unit produced by TypeChecker and consumed by FunctionEmitter.
// All traversals use C# switch pattern matching (exhaustive by convention).

/// A list of bound statements with their source span.
/// Used as function bodies, if-branches, loop bodies, etc.
public sealed record BoundBlock(IReadOnlyList<BoundStmt> Stmts, Span Span);

public abstract record BoundStmt(Span Span);

// ── Block as statement ────────────────────────────────────────────────────────

public sealed record BoundBlockStmt(BoundBlock Block, Span Span) : BoundStmt(Span);

// ── Variable declaration ──────────────────────────────────────────────────────

/// Bound variable declaration. VarType is already resolved; never Z42Type.Unknown
/// unless the variable has no annotation and no initializer.
public sealed record BoundVarDecl(
    string Name,
    Z42Type VarType,
    BoundExpr? Init,
    Span Span) : BoundStmt(Span);

// ── Return ────────────────────────────────────────────────────────────────────

public sealed record BoundReturn(BoundExpr? Value, Span Span) : BoundStmt(Span);

// ── Expression statement ──────────────────────────────────────────────────────

public sealed record BoundExprStmt(BoundExpr Expr, Span Span) : BoundStmt(Span);

// ── Control flow ──────────────────────────────────────────────────────────────

public sealed record BoundIf(
    BoundExpr Cond,
    BoundBlock Then,
    BoundStmt? Else,
    Span Span) : BoundStmt(Span);

public sealed record BoundWhile(BoundExpr Cond, BoundBlock Body, Span Span) : BoundStmt(Span);

public sealed record BoundDoWhile(BoundBlock Body, BoundExpr Cond, Span Span) : BoundStmt(Span);

public sealed record BoundFor(
    BoundStmt? Init,
    BoundExpr? Cond,
    BoundExpr? Increment,
    BoundBlock Body,
    Span Span) : BoundStmt(Span);

public sealed record BoundForeach(
    string VarName,
    Z42Type VarType,
    BoundExpr Collection,
    BoundBlock Body,
    Span Span) : BoundStmt(Span);

public sealed record BoundBreak(Span Span) : BoundStmt(Span);

public sealed record BoundContinue(Span Span) : BoundStmt(Span);

// ── Switch ────────────────────────────────────────────────────────────────────

public sealed record BoundSwitch(
    BoundExpr Subject,
    IReadOnlyList<BoundSwitchCase> Cases,
    Span Span) : BoundStmt(Span);

public sealed record BoundSwitchCase(
    BoundExpr? Pattern,
    IReadOnlyList<BoundStmt> Body,
    Span Span);

// ── Try / catch ───────────────────────────────────────────────────────────────

public sealed record BoundTryCatch(
    BoundBlock TryBody,
    IReadOnlyList<BoundCatchClause> Catches,
    BoundBlock? Finally,
    Span Span) : BoundStmt(Span);

public sealed record BoundCatchClause(string? VarName, BoundBlock Body, Span Span);

// ── Throw ─────────────────────────────────────────────────────────────────────

public sealed record BoundThrow(BoundExpr Value, Span Span) : BoundStmt(Span);
