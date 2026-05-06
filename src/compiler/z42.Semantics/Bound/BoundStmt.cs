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

/// `catch (T e) { ... }` after binding.
///
/// `ExceptionTypeName` carries the resolved fully-qualified type name (e.g.
/// `Std.IOException`) when the user declared a typed catch. `null` means an
/// untyped `catch { }` / `catch (e)` — VM treats it as a wildcard.
/// TypeChecker validates that the named type is a known class deriving from
/// `Std.Exception`; failures emit E0420 and leave `ExceptionTypeName` as null.
/// (catch-by-generic-type, 2026-05-06)
public sealed record BoundCatchClause(
    string? VarName,
    string? ExceptionTypeName,
    BoundBlock Body,
    Span Span);

// ── Throw ─────────────────────────────────────────────────────────────────────

public sealed record BoundThrow(BoundExpr Value, Span Span) : BoundStmt(Span);

// ── Local function (impl-local-fn-l2) ────────────────────────────────────────

/// Local (nested) function declaration. Lifted to a module-level function during
/// IrGen with name `<Owner>__<LocalName>`. `Captures` lists outer-scope variables
/// referenced in the body — empty for the L2 no-capture path (lifts via direct
/// `Call`); non-empty triggers the L3 `MkClos` heap-erasure path.
/// See docs/design/closure.md §3.4 + impl-closure-l3-core.
public sealed record BoundLocalFunction(
    string Name,
    IReadOnlyList<string> ParamNames,
    IReadOnlyList<Z42Type> ParamTypes,
    Z42Type RetType,
    BoundBlock Body,
    IReadOnlyList<BoundCapture> Captures,
    Span Span) : BoundStmt(Span);

// ── Pinned (spec C5) ────────────────────────────────────────────────────────

/// `pinned <Name> = <Source> { <Body> }` — Tier 1 native-interop borrow.
/// Codegen emits `PinPtrInstr` before <Body>, the body itself, and a
/// terminating `UnpinPtrInstr`. The TypeChecker rejects early control
/// flow inside the body so each pin pairs with exactly one unpin.
public sealed record BoundPinned(
    string Name,
    BoundExpr Source,
    BoundBlock Body,
    Span Span) : BoundStmt(Span);
