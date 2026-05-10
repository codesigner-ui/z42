using Z42.Core.Text;

namespace Z42.Syntax.Lexer;

/// <summary>
/// A lexed token.
///
/// <para><c>LeadingTrivia</c> (lexer-trivia-preserve, 2026-05-10): the raw
/// source text of all whitespace + comments immediately preceding this token.
/// Always populated by the lexer (may be empty string, never null in practice).
/// Reserved for future formatter / IDE / round-trip-preserving codegen — the
/// AST and TypeChecker pipelines currently ignore it. Leading-only model
/// (Roslyn-lite): every skipped char belongs to the FOLLOWING token; trailing
/// trivia is the next token's leading. The end-of-file token carries any
/// trailing trivia (whitespace/comments at the end of source).</para>
///
/// Adding the field as a positional record property with a default value keeps
/// existing 3-arg construction sites working unchanged.
/// </summary>
public readonly record struct Token(
    TokenKind Kind,
    string Text,
    Span Span,
    string LeadingTrivia = "")
{
    public static Token Eof(int pos, string file = "", string leadingTrivia = "")
        => new(TokenKind.Eof, "", new Span(pos, pos, 0, 0, file), leadingTrivia);

    public override string ToString() => $"{Kind}({Text}) @{Span.Line}:{Span.Column}";
}
