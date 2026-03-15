namespace Z42.Compiler.Lexer;

public readonly record struct Span(int Start, int End, int Line, int Column);

public readonly record struct Token(TokenKind Kind, string Text, Span Span)
{
    public static Token Eof(int pos) => new(TokenKind.Eof, "", new Span(pos, pos, 0, 0));
    public override string ToString() => $"{Kind}({Text}) @{Span.Line}:{Span.Column}";
}
