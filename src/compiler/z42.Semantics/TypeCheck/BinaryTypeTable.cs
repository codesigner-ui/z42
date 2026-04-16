namespace Z42.Semantics.TypeCheck;

/// Per-operator type rule used by TypeChecker to validate binary expressions.
///
/// LeftOk / RightOk: null = no constraint (any type accepted without error).
/// Requirement: human-readable word used in error messages ("numeric", "bool", "integral").
/// Output: computes the result type from the two operand types.
internal sealed record BinaryTypeRule(
    Func<Z42Type, bool>?            LeftOk,
    Func<Z42Type, bool>?            RightOk,
    Func<Z42Type, Z42Type, Z42Type> Output,
    string                          Requirement);

/// Per-operator type rule for unary/prefix/postfix expressions.
///
/// Constraint: null = no constraint; otherwise predicate + error word.
/// Output: computes the result type from the operand type.
internal sealed record UnaryTypeRule(
    Func<Z42Type, bool>?    Constraint,
    Func<Z42Type, Z42Type>  Output,
    string                  Requirement);

/// Static table mapping operator string → its type rule.
///
/// Design: adding a new operator requires exactly ONE line here — no changes
/// to TypeChecker.Exprs.cs needed.  This mirrors the ParseTable approach used
/// by the parser.
///
/// Special case NOT in this table:
///   "+"  with at least one string operand → string concatenation
///   TypeChecker.CheckBinary handles this as a pre-check before table lookup.
internal static class BinaryTypeTable
{
    private static readonly Func<Z42Type, bool> Numeric  = Z42Type.IsNumeric;
    private static readonly Func<Z42Type, bool> Integral = Z42Type.IsIntegral;
    private static readonly Func<Z42Type, bool> Bool     = Z42Type.IsBool;

    private static Z42Type BoolOut (Z42Type _,  Z42Type __) => Z42Type.Bool;
    private static Z42Type ArithOut(Z42Type l,  Z42Type r)  => Z42Type.ArithmeticResult(l, r);
    private static Z42Type UnkOut  (Z42Type _,  Z42Type __) => Z42Type.Unknown;

    internal static readonly IReadOnlyDictionary<string, BinaryTypeRule> Rules =
        new Dictionary<string, BinaryTypeRule>
        {
            // ── Arithmetic: both operands numeric → wider type ─────────────────
            ["+"]  = new(Numeric,  Numeric,  ArithOut, "numeric"),
            ["-"]  = new(Numeric,  Numeric,  ArithOut, "numeric"),
            ["*"]  = new(Numeric,  Numeric,  ArithOut, "numeric"),
            ["/"]  = new(Numeric,  Numeric,  ArithOut, "numeric"),
            ["%"]  = new(Numeric,  Numeric,  ArithOut, "numeric"),

            // ── Relational: left numeric → bool ───────────────────────────────
            ["<"]  = new(Numeric,  null, BoolOut, "numeric"),
            ["<="] = new(Numeric,  null, BoolOut, "numeric"),
            [">"]  = new(Numeric,  null, BoolOut, "numeric"),
            [">="] = new(Numeric,  null, BoolOut, "numeric"),

            // ── Equality: no constraint → bool ────────────────────────────────
            ["=="] = new(null, null, BoolOut, ""),
            ["!="] = new(null, null, BoolOut, ""),

            // ── Logical: both bool → bool ─────────────────────────────────────
            ["&&"] = new(Bool, Bool, BoolOut, "bool"),
            ["||"] = new(Bool, Bool, BoolOut, "bool"),

            // ── Bitwise / shift: both integral → wider type ───────────────────
            ["&"]  = new(Integral, Integral, ArithOut, "integral"),
            ["|"]  = new(Integral, Integral, ArithOut, "integral"),
            ["^"]  = new(Integral, Integral, ArithOut, "integral"),
            ["<<"] = new(Integral, Integral, ArithOut, "integral"),
            [">>"] = new(Integral, Integral, ArithOut, "integral"),

            // ── Type tests: no operand constraint ─────────────────────────────
            ["is"] = new(null, null, BoolOut, ""),
            ["as"] = new(null, null, UnkOut,  ""),
        };
}

/// Static table mapping unary operator string → its type rule.
/// Parallel to BinaryTypeTable: adding a new unary op requires ONE line.
internal static class UnaryTypeTable
{
    private static readonly Func<Z42Type, bool> Numeric  = Z42Type.IsNumeric;
    private static readonly Func<Z42Type, bool> Integral = Z42Type.IsIntegral;
    private static readonly Func<Z42Type, bool> BoolPred = Z42Type.IsBool;

    private static Z42Type Passthrough(Z42Type t)  => t;
    private static Z42Type BoolOut(Z42Type _)       => Z42Type.Bool;
    private static Z42Type UnknownOut(Z42Type _)    => Z42Type.Unknown;

    internal static readonly IReadOnlyDictionary<string, UnaryTypeRule> Rules =
        new Dictionary<string, UnaryTypeRule>
        {
            ["!"]     = new(BoolPred, BoolOut,      "bool"),
            ["-"]     = new(Numeric,  Passthrough,  "numeric"),
            ["+"]     = new(Numeric,  Passthrough,  "numeric"),
            ["~"]     = new(Integral, Passthrough,  "integral"),
            ["++"]    = new(Numeric,  Passthrough,  "numeric"),
            ["--"]    = new(Numeric,  Passthrough,  "numeric"),
            ["await"] = new(null,     UnknownOut,   ""),
        };
}
