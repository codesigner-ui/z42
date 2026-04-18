using Z42.Core.Text;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Bound;

// ── Bound expression hierarchy ────────────────────────────────────────────────
// Every BoundExpr carries its inferred Z42Type; no dictionary lookup needed.

public abstract record BoundExpr(Z42Type Type, Span Span)
{
    public abstract TResult Accept<TResult>(IBoundExprVisitor<TResult> visitor);
}

// ── Literals ──────────────────────────────────────────────────────────────────

public sealed record BoundLitInt(long Value, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitLitInt(this); }

public sealed record BoundLitFloat(double Value, bool IsFloat, Span Span)
    : BoundExpr(IsFloat ? Z42Type.Float : Z42Type.Double, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitLitFloat(this); }

public sealed record BoundLitStr(string Value, Span Span)
    : BoundExpr(Z42Type.String, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitLitStr(this); }

public sealed record BoundLitBool(bool Value, Span Span)
    : BoundExpr(Z42Type.Bool, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitLitBool(this); }

public sealed record BoundLitNull(Span Span)
    : BoundExpr(Z42Type.Null, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitLitNull(this); }

public sealed record BoundLitChar(char Value, Span Span)
    : BoundExpr(Z42Type.Char, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitLitChar(this); }

// ── Interpolated string ───────────────────────────────────────────────────────

public sealed record BoundInterpolatedStr(
    IReadOnlyList<BoundInterpolationPart> Parts,
    Span Span) : BoundExpr(Z42Type.String, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitInterpolatedStr(this); }

public abstract record BoundInterpolationPart(Span Span);
public sealed record BoundTextPart(string Text, Span Span) : BoundInterpolationPart(Span);
public sealed record BoundExprPart(BoundExpr Inner, Span Span) : BoundInterpolationPart(Span);

// ── Operator enums ────────────────────────────────────────────────────────────

public enum BinaryOp
{
    Add, Sub, Mul, Div, Rem,
    Eq, Ne, Lt, Le, Gt, Ge,
    And, Or,
    BitAnd, BitOr, BitXor, Shl, Shr,
    Is, As,
}

public enum UnaryOp  { Neg, Plus, Not, BitNot, PrefixInc, PrefixDec, Await }
public enum PostfixOp { Inc, Dec }

// ── Core expressions ──────────────────────────────────────────────────────────

public sealed record BoundIdent(string Name, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitIdent(this); }

public sealed record BoundAssign(BoundExpr Target, BoundExpr Value, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitAssign(this); }

public sealed record BoundBinary(BinaryOp Op, BoundExpr Left, BoundExpr Right, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitBinary(this); }

public sealed record BoundUnary(UnaryOp Op, BoundExpr Operand, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitUnary(this); }

public sealed record BoundPostfix(PostfixOp Op, BoundExpr Operand, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitPostfix(this); }

// ── Calls ─────────────────────────────────────────────────────────────────────

/// Classifies how a call was resolved by the TypeChecker.
/// All calls are fully resolved at TypeCheck time (stdlib methods resolved via DepIndex).
public enum BoundCallKind { Free, Static, Instance, Virtual }

/// A fully-bound call expression.
///
/// Field usage by kind:
///   Free       — CalleeName = func name, Receiver = null
///   Static     — ReceiverClass = class, MethodName = method, Receiver = null
///   Instance   — Receiver = bound receiver, ReceiverClass = class, MethodName = method
///   Virtual    — Receiver = bound receiver, ReceiverClass = class, MethodName = method
public sealed record BoundCall(
    BoundCallKind Kind,
    BoundExpr? Receiver,
    string? ReceiverClass,
    string? MethodName,
    string? CalleeName,
    IReadOnlyList<BoundExpr> Args,
    Z42Type RetType,
    Span Span) : BoundExpr(RetType, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitCall(this); }

// ── Member and index access ───────────────────────────────────────────────────

public sealed record BoundMember(BoundExpr Target, string MemberName, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitMember(this); }

public sealed record BoundIndex(BoundExpr Target, BoundExpr Index, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitIndex(this); }

// ── Type operations ───────────────────────────────────────────────────────────

public sealed record BoundCast(BoundExpr Operand, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitCast(this); }

public sealed record BoundNew(
    string QualName,
    IReadOnlyList<BoundExpr> Args,
    Z42Type Type,
    Span Span) : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitNew(this); }

public sealed record BoundArrayCreate(BoundExpr Size, Z42Type ElemType, Span Span)
    : BoundExpr(new Z42ArrayType(ElemType), Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitArrayCreate(this); }

public sealed record BoundArrayLit(
    IReadOnlyList<BoundExpr> Elements,
    Z42Type ElemType,
    Span Span) : BoundExpr(new Z42ArrayType(ElemType), Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitArrayLit(this); }

// ── Control-flow expressions ──────────────────────────────────────────────────

public sealed record BoundConditional(
    BoundExpr Cond, BoundExpr Then, BoundExpr Else, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitConditional(this); }

public sealed record BoundNullCoalesce(BoundExpr Left, BoundExpr Right, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitNullCoalesce(this); }

public sealed record BoundNullConditional(BoundExpr Target, string MemberName, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitNullConditional(this); }

public sealed record BoundIsPattern(
    BoundExpr Target,
    string TypeName,
    string Binding,
    Z42Type BindType,
    Span Span) : BoundExpr(Z42Type.Bool, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitIsPattern(this); }

public sealed record BoundSwitchExpr(
    BoundExpr Subject,
    IReadOnlyList<BoundSwitchArm> Arms,
    Z42Type Type,
    Span Span) : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitSwitchExpr(this); }

public sealed record BoundSwitchArm(BoundExpr? Pattern, BoundExpr Body, Span Span);

// ── Error sentinel ────────────────────────────────────────────────────────────

public sealed record BoundError(string Message, Z42Type Type, Span Span)
    : BoundExpr(Type, Span)
{ public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v) => v.VisitError(this); }
