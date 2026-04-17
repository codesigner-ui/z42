using Z42.Core.Features;
using Z42.Syntax.Lexer;

namespace Z42.Syntax.Parser;

/// <summary>
/// Associativity for binary operators.
/// </summary>
internal enum Assoc { Left, Right }

/// <summary>
/// Single source of truth for binary operator metadata.
/// Drives ExprParser (precedence/associativity) and serves as the canonical mapping
/// from TokenKind → op string used by TypeChecker (ToBinaryOp) and FunctionEmitter.
///
/// To add a new binary operator: add ONE entry here + the TokenKind.
/// </summary>
internal static class OperatorDefs
{
    internal sealed record BinaryOpDef(
        TokenKind       Token,
        string          OpStr,
        int             Bp,
        Assoc           Assoc,
        LanguageFeature? Feature = null);

    // ── Binding-power constants ──────────────────────────────────────────────

    internal const int BpLogicalOr   = 30;
    internal const int BpLogicalAnd  = 40;
    internal const int BpBitwiseOr   = 44;
    internal const int BpBitwiseXor  = 46;
    internal const int BpBitwiseAnd  = 48;
    internal const int BpEquality    = 50;
    internal const int BpRelational  = 60;
    internal const int BpShift       = 65;
    internal const int BpAdditive    = 70;
    internal const int BpMultiply    = 80;

    // ── Binary operator table ────────────────────────────────────────────────

    internal static readonly IReadOnlyList<BinaryOpDef> BinaryOps =
    [
        // Logical
        new(TokenKind.PipePipe, "||", BpLogicalOr,  Assoc.Left),
        new(TokenKind.AmpAmp,   "&&", BpLogicalAnd, Assoc.Left),

        // Bitwise
        new(TokenKind.Pipe,      "|",  BpBitwiseOr,  Assoc.Left, LanguageFeature.Bitwise),
        new(TokenKind.Caret,     "^",  BpBitwiseXor, Assoc.Left, LanguageFeature.Bitwise),
        new(TokenKind.Ampersand, "&",  BpBitwiseAnd, Assoc.Left, LanguageFeature.Bitwise),

        // Equality
        new(TokenKind.EqEq,   "==", BpEquality, Assoc.Left),
        new(TokenKind.BangEq, "!=", BpEquality, Assoc.Left),

        // Relational
        new(TokenKind.Lt,   "<",  BpRelational, Assoc.Left),
        new(TokenKind.LtEq, "<=", BpRelational, Assoc.Left),
        new(TokenKind.Gt,   ">",  BpRelational, Assoc.Left),
        new(TokenKind.GtEq, ">=", BpRelational, Assoc.Left),

        // Shifts
        new(TokenKind.LtLt, "<<", BpShift, Assoc.Left, LanguageFeature.Bitwise),
        new(TokenKind.GtGt, ">>", BpShift, Assoc.Left, LanguageFeature.Bitwise),

        // Additive
        new(TokenKind.Plus,  "+", BpAdditive, Assoc.Left),
        new(TokenKind.Minus, "-", BpAdditive, Assoc.Left),

        // Multiplicative
        new(TokenKind.Star,    "*", BpMultiply, Assoc.Left),
        new(TokenKind.Slash,   "/", BpMultiply, Assoc.Left),
        new(TokenKind.Percent, "%", BpMultiply, Assoc.Left),
    ];

    // ── Derived lookups ──────────────────────────────────────────────────────

    /// TokenKind → BinaryOpDef for quick lookup during parsing.
    internal static readonly IReadOnlyDictionary<TokenKind, BinaryOpDef> ByToken =
        BinaryOps.ToDictionary(d => d.Token);

    /// Op string → BinaryOpDef for TypeChecker / codegen lookup.
    internal static readonly IReadOnlyDictionary<string, BinaryOpDef> ByOpStr =
        BinaryOps.ToDictionary(d => d.OpStr);
}
