using Z42.Core.Text;

namespace Z42.Syntax.Lexer;

public readonly record struct Token(TokenKind Kind, string Text, Span Span)
{
    public static Token Eof(int pos, string file = "") => new(TokenKind.Eof, "", new Span(pos, pos, 0, 0, file));
    public override string ToString() => $"{Kind}({Text}) @{Span.Line}:{Span.Column}";
}
