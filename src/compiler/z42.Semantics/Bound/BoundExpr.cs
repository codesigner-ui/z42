using Z42.Core.Text;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Bound;

// ── Bound expression hierarchy ────────────────────────────────────────────────
// Every BoundExpr carries its inferred Z42Type; no dictionary lookup needed.
// All traversals use C# switch pattern matching (exhaustive by convention).

public abstract record BoundExpr(Z42Type Type, Span Span);

// ── Literals ──────────────────────────────────────────────────────────────────

public sealed record BoundLitInt(long Value, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);

public sealed record BoundLitFloat(double Value, bool IsFloat, Span Span)
    : BoundExpr(IsFloat ? Z42Type.Float : Z42Type.Double, Span);

public sealed record BoundLitStr(string Value, Span Span)
    : BoundExpr(Z42Type.String, Span);

public sealed record BoundLitBool(bool Value, Span Span)
    : BoundExpr(Z42Type.Bool, Span);

public sealed record BoundLitNull(Span Span)
    : BoundExpr(Z42Type.Null, Span);

public sealed record BoundLitChar(char Value, Span Span)
    : BoundExpr(Z42Type.Char, Span);

// ── Interpolated string ───────────────────────────────────────────────────────

public sealed record BoundInterpolatedStr(
    IReadOnlyList<BoundInterpolationPart> Parts,
    Span Span) : BoundExpr(Z42Type.String, Span);

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
    : BoundExpr(Type, Span);

public sealed record BoundAssign(BoundExpr Target, BoundExpr Value, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);

public sealed record BoundBinary(BinaryOp Op, BoundExpr Left, BoundExpr Right, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);

public sealed record BoundUnary(UnaryOp Op, BoundExpr Operand, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);

public sealed record BoundPostfix(PostfixOp Op, BoundExpr Operand, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);

// ── Lambda ────────────────────────────────────────────────────────────────────

/// L2 no-capture lambda parameter (typed) — Type is fully resolved.
public sealed record BoundLambdaParam(string Name, Z42Type Type, Span Span);

/// L2 no-capture lambda body: either a single expression or a block.
public abstract record BoundLambdaBody(Span Span);
public sealed record BoundLambdaExprBody(BoundExpr Expr, Span Span)         : BoundLambdaBody(Span);
public sealed record BoundLambdaBlockBody(BoundBlock Block, Span Span)      : BoundLambdaBody(Span);

/// L3 lambda capture kind. See docs/design/closure.md §4 + impl-closure-l3-core.
/// - `ValueSnapshot` — value type captured by copy at MkClos time; closure body
///   sees the captured value frozen at creation. Modifying it affects the
///   closure's env only, never the outer variable.
/// - `ReferenceShare` — reference type captured by identity; mutations to the
///   shared object are visible both inside the closure and from the outer scope.
public enum BoundCaptureKind { ValueSnapshot, ReferenceShare }

/// A single captured variable inside a lambda or local function.
/// Determined at TypeCheck time; consumed by Codegen to emit `MkClos`.
public sealed record BoundCapture(string Name, Z42Type Type, BoundCaptureKind Kind, Span Span);

/// Reference to a captured variable inside a lambda / local fn body.
/// Replaces `BoundIdent` for names that resolve through the lambda's outer
/// boundary. `CaptureIndex` indexes into the lifted function's `env` array
/// (env is a `Vec<Value>` in the VM — see closure.md §4 + design.md Decision 1).
public sealed record BoundCapturedIdent(
    string Name,
    Z42Type Type,
    int CaptureIndex,
    Span Span) : BoundExpr(Type, Span);

/// Lambda literal — captured into a `Z42FuncType`. `Captures` lists the
/// outer-scope variables read in the body (in capture order); empty for
/// the L2 no-capture path (lifts via `LoadFn`). Non-empty triggers L3
/// `MkClos` lifting with an env. See closure.md §6.
///
/// 是否栈分配 env 通过 `SemanticModel.StackAllocClosures` 集合查询
/// （reference-equality keyed），由 ClosureEscapeAnalyzer 写回。BoundLambda
/// 本身保持 immutable record。详见 impl-closure-l3-escape-stack。
public sealed record BoundLambda(
    IReadOnlyList<BoundLambdaParam> Params,
    BoundLambdaBody Body,
    Z42FuncType FuncType,
    IReadOnlyList<BoundCapture> Captures,
    Span Span) : BoundExpr(FuncType, Span);

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
    Span Span) : BoundExpr(RetType, Span);

// ── Parameter modifier wrapper (spec: define-ref-out-in-parameters) ──────────

/// Inline `out var x` declaration carried by a BoundModifiedArg. The local
/// `Name` is registered in the caller's TypeEnv at bind time (with placeholder
/// `Z42Type.Unknown` if signature not yet resolved); CheckArgTypes patches the
/// type from the matched parameter. `Type` is the resolved local type after
/// signature matching.
public sealed record BoundOutVarDecl(string Name, Z42Type Type, Span Span);

/// Bound form of `Syntax.Parser.ModifiedArg` — a callsite argument that the
/// user wrote with `ref` / `out` / `in` prefix. `Inner` is the bound inner
/// expression (typically a `BoundIdent` for ref/in, the just-declared local
/// `BoundIdent` for `out var x`, or any lvalue expression like
/// `BoundIndex` / `BoundMember` for `ref a[i]` / `ref obj.f`). `Type` mirrors
/// `Inner.Type` so existing code paths that read `BoundExpr.Type` keep working.
/// IR codegen unwraps to emit address-load + ref-mask bit for the matching
/// argument slot (see design.md Decision 1).
public sealed record BoundModifiedArg(
    BoundExpr Inner,
    Z42.Syntax.Parser.ArgModifier Modifier,
    BoundOutVarDecl? OutDecl,
    Z42Type Type,
    Span Span) : BoundExpr(Type, Span);

// ── Member and index access ───────────────────────────────────────────────────

public sealed record BoundMember(BoundExpr Target, string MemberName, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);

public sealed record BoundIndex(BoundExpr Target, BoundExpr Index, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);

// ── Type operations ───────────────────────────────────────────────────────────

public sealed record BoundCast(BoundExpr Operand, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);

public sealed record BoundNew(
    string QualName,
    /// FQ ctor function name selected by overload resolution.
    /// Single ctor: `"ClassName.SimpleName"` (no suffix);
    /// overloaded:  `"ClassName.SimpleName$N"`. Codegen passes this verbatim
    /// to ObjNewInstr.CtorName so VM can dispatch without name inference.
    string CtorName,
    IReadOnlyList<BoundExpr> Args,
    Z42Type Type,
    Span Span) : BoundExpr(Type, Span);

public sealed record BoundArrayCreate(BoundExpr Size, Z42Type ElemType, Span Span)
    : BoundExpr(new Z42ArrayType(ElemType), Span);

public sealed record BoundArrayLit(
    IReadOnlyList<BoundExpr> Elements,
    Z42Type ElemType,
    Span Span) : BoundExpr(new Z42ArrayType(ElemType), Span);

// ── Control-flow expressions ──────────────────────────────────────────────────

public sealed record BoundConditional(
    BoundExpr Cond, BoundExpr Then, BoundExpr Else, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);

public sealed record BoundNullCoalesce(BoundExpr Left, BoundExpr Right, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);

public sealed record BoundNullConditional(BoundExpr Target, string MemberName, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);

public sealed record BoundIsPattern(
    BoundExpr Target,
    string TypeName,
    string Binding,
    Z42Type BindType,
    Span Span) : BoundExpr(Z42Type.Bool, Span);

public sealed record BoundSwitchExpr(
    BoundExpr Subject,
    IReadOnlyList<BoundSwitchArm> Arms,
    Z42Type Type,
    Span Span) : BoundExpr(Type, Span);

public sealed record BoundSwitchArm(BoundExpr? Pattern, BoundExpr Body, Span Span);

// ── Error sentinel ────────────────────────────────────────────────────────────

public sealed record BoundError(string Message, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);
