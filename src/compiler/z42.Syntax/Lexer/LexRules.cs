using static Z42.Syntax.Lexer.LexCombinators;

namespace Z42.Syntax.Lexer;

/// Combinator-based lexer rules — the user-extensible layer.
/// Rules here are composed from LexCombinators primitives (Many1/Seq/Or/…).
///
/// To add a new numeric format : add one entry to NumericRules (tried in order; first match wins).
/// To add a new string prefix  : add one entry to StringRules (longest prefix first).
///
/// Example — enable octal literals:
///   new NumericRule(Seq(LitI("0o"), Many1(c => c >= '0' && c &lt;= '7', '_')), TokenKind.IntLiteral)
internal static class LexRules
{
    // ── Numeric rules ─────────────────────────────────────────────────────────
    // Tried in order; first match wins.

    internal sealed record NumericRule(LexRule Rule, TokenKind Kind);

    // Base digit predicates
    private static readonly LexRule s_decDigits = Many1(char.IsAsciiDigit,    '_');
    private static readonly LexRule s_hexDigits = Many1(char.IsAsciiHexDigit, '_');
    private static readonly LexRule s_binDigits = Many1(c => c is '0' or '1', '_');

    // Decimal float: digits ( .digits )? ( [eE] [+-]? digits )?
    // Must have at least one of fraction or exponent to be a float.
    private static readonly LexRule s_optExponent =
        Opt(Seq(OneOf('e', 'E'), Opt(OneOf('+', '-')), s_decDigits));
    private static readonly LexRule s_decFloat =
        Seq(
            s_decDigits,
            Or(
                // fraction only (with optional exponent)
                Seq(Char('.'), s_decDigits, s_optExponent),
                // exponent only (no decimal point)
                Seq(OneOf('e', 'E'), Opt(OneOf('+', '-')), s_decDigits)
            )
        );

    internal static readonly IReadOnlyList<NumericRule> NumericRules =
    [
        new(Seq(LitI("0x"), s_hexDigits), TokenKind.IntLiteral),
        new(Seq(LitI("0b"), s_binDigits), TokenKind.IntLiteral),
        // To add octal: new(Seq(LitI("0o"), Many1(c => c >= '0' && c <= '7', '_')), TokenKind.IntLiteral),
        new(s_decFloat,                   TokenKind.FloatLiteral),
        new(s_decDigits,                  TokenKind.IntLiteral),    // decimal int (fallback)
    ];

    // ── String / char rules ───────────────────────────────────────────────────
    // Prefix-matched in order (longest prefix first).
    // The Lexer handles body lexing (escapes, interpolation nesting) —
    // only the opening prefix and result kind are configured here.

    internal sealed record StringRule(string Prefix, TokenKind Kind,
        bool IsChar = false, bool IsInterpolated = false);

    internal static readonly IReadOnlyList<StringRule> StringRules =
    [
        new("$\"", TokenKind.InterpolatedStringLiteral, IsInterpolated: true),
        new("\"",  TokenKind.StringLiteral),
        new("'",   TokenKind.CharLiteral, IsChar: true),
        // To add verbatim strings: new("@\"", TokenKind.RawStringLiteral),
    ];
}
