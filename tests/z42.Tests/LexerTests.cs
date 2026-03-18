using FluentAssertions;
using Z42.Compiler.Lexer;

namespace Z42.Tests;

/// Unit tests for the Lexer: one test per token category, covering
/// keywords, literals, operators, compound assignments, and edge cases.
public sealed class LexerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<Token> Lex(string src)
        => new Lexer(src).Tokenize();

    /// Returns every token kind except the trailing EOF.
    private static List<TokenKind> Kinds(string src)
        => Lex(src).Where(t => t.Kind != TokenKind.Eof).Select(t => t.Kind).ToList();

    // ── Control flow keywords ─────────────────────────────────────────────────

    [Theory]
    [InlineData("if",       TokenKind.If)]
    [InlineData("else",     TokenKind.Else)]
    [InlineData("while",    TokenKind.While)]
    [InlineData("for",      TokenKind.For)]
    [InlineData("foreach",  TokenKind.Foreach)]
    [InlineData("in",       TokenKind.In)]
    [InlineData("break",    TokenKind.Break)]
    [InlineData("continue", TokenKind.Continue)]
    [InlineData("return",   TokenKind.Return)]
    public void Keyword_IsRecognised(string text, TokenKind expected)
    {
        var tokens = Lex(text);
        tokens.Should().HaveCount(2);          // keyword + EOF
        tokens[0].Kind.Should().Be(expected);
        tokens[0].Text.Should().Be(text);
    }

    // ── Declaration keywords ──────────────────────────────────────────────────

    [Theory]
    [InlineData("var",   TokenKind.Var)]
    [InlineData("new",   TokenKind.New)]
    [InlineData("null",  TokenKind.Null)]
    [InlineData("true",  TokenKind.True)]
    [InlineData("false", TokenKind.False)]
    public void DeclarationKeyword_IsRecognised(string text, TokenKind expected)
    {
        Lex(text)[0].Kind.Should().Be(expected);
    }

    // ── Built-in types ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("int",    TokenKind.Int)]
    [InlineData("long",   TokenKind.Long)]
    [InlineData("short",  TokenKind.Short)]
    [InlineData("double", TokenKind.Double)]
    [InlineData("float",  TokenKind.Float)]
    [InlineData("byte",   TokenKind.Byte)]
    [InlineData("bool",   TokenKind.Bool)]
    [InlineData("string", TokenKind.String)]
    [InlineData("void",   TokenKind.Void)]
    [InlineData("char",   TokenKind.Char)]
    [InlineData("object", TokenKind.Object)]
    public void TypeKeyword_IsRecognised(string text, TokenKind expected)
    {
        Lex(text)[0].Kind.Should().Be(expected);
    }

    // ── Integer literal ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("0")]
    [InlineData("42")]
    [InlineData("1000000")]
    public void IntLiteral_IsRecognised(string src)
    {
        Lex(src)[0].Kind.Should().Be(TokenKind.IntLiteral);
    }

    [Fact]
    public void IntLiteral_WithLSuffix_IsStillIntLiteral()
    {
        Lex("99L")[0].Kind.Should().Be(TokenKind.IntLiteral);
    }

    // ── Float literal ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("3.14")]
    [InlineData("0.0")]
    public void FloatLiteral_IsRecognised(string src)
    {
        Lex(src)[0].Kind.Should().Be(TokenKind.FloatLiteral);
    }

    // ── String literal ────────────────────────────────────────────────────────

    [Fact]
    public void StringLiteral_BasicQuoted()
    {
        var tok = Lex("\"hello\"")[0];
        tok.Kind.Should().Be(TokenKind.StringLiteral);
        tok.Text.Should().Be("\"hello\"");
    }

    [Fact]
    public void InterpolatedString_IsRecognised()
    {
        Lex("$\"value\"")[0].Kind.Should().Be(TokenKind.InterpolatedStringLiteral);
    }

    // ── Char literal ──────────────────────────────────────────────────────────

    [Fact]
    public void CharLiteral_IsRecognised()
    {
        Lex("'a'")[0].Kind.Should().Be(TokenKind.CharLiteral);
    }

    // ── Arithmetic operators ──────────────────────────────────────────────────

    [Theory]
    [InlineData("+",  TokenKind.Plus)]
    [InlineData("-",  TokenKind.Minus)]
    [InlineData("*",  TokenKind.Star)]
    [InlineData("/",  TokenKind.Slash)]
    [InlineData("%",  TokenKind.Percent)]
    public void ArithmeticOp_IsRecognised(string src, TokenKind expected)
    {
        Lex(src)[0].Kind.Should().Be(expected);
    }

    // ── Comparison operators ──────────────────────────────────────────────────

    [Theory]
    [InlineData("==", TokenKind.EqEq)]
    [InlineData("!=", TokenKind.BangEq)]
    [InlineData("<",  TokenKind.Lt)]
    [InlineData("<=", TokenKind.LtEq)]
    [InlineData(">",  TokenKind.Gt)]
    [InlineData(">=", TokenKind.GtEq)]
    public void ComparisonOp_IsRecognised(string src, TokenKind expected)
    {
        Lex(src)[0].Kind.Should().Be(expected);
    }

    // ── Logical operators ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("&&", TokenKind.AmpAmp)]
    [InlineData("||", TokenKind.PipePipe)]
    [InlineData("!",  TokenKind.Bang)]
    public void LogicalOp_IsRecognised(string src, TokenKind expected)
    {
        Lex(src)[0].Kind.Should().Be(expected);
    }

    // ── Compound assignment operators ─────────────────────────────────────────

    [Theory]
    [InlineData("+=", TokenKind.PlusEq)]
    [InlineData("-=", TokenKind.MinusEq)]
    [InlineData("*=", TokenKind.StarEq)]
    [InlineData("/=", TokenKind.SlashEq)]
    [InlineData("%=", TokenKind.PercentEq)]
    public void CompoundAssignOp_IsRecognised(string src, TokenKind expected)
    {
        Lex(src)[0].Kind.Should().Be(expected);
    }

    // ── Increment / decrement ─────────────────────────────────────────────────

    [Theory]
    [InlineData("++", TokenKind.PlusPlus)]
    [InlineData("--", TokenKind.MinusMinus)]
    public void IncrDecrOp_IsRecognised(string src, TokenKind expected)
    {
        Lex(src)[0].Kind.Should().Be(expected);
    }

    // ── Delimiters ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{", TokenKind.LBrace)]
    [InlineData("}", TokenKind.RBrace)]
    [InlineData("(", TokenKind.LParen)]
    [InlineData(")", TokenKind.RParen)]
    [InlineData("[", TokenKind.LBracket)]
    [InlineData("]", TokenKind.RBracket)]
    [InlineData(";", TokenKind.Semicolon)]
    [InlineData(",", TokenKind.Comma)]
    [InlineData(".", TokenKind.Dot)]
    [InlineData("=", TokenKind.Eq)]
    public void Delimiter_IsRecognised(string src, TokenKind expected)
    {
        Lex(src)[0].Kind.Should().Be(expected);
    }

    // ── Identifier ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("x")]
    [InlineData("myVar")]
    [InlineData("_private")]
    [InlineData("CamelCase")]
    [InlineData("x1")]
    public void Identifier_IsRecognised(string src)
    {
        var tok = Lex(src)[0];
        tok.Kind.Should().Be(TokenKind.Identifier);
        tok.Text.Should().Be(src);
    }

    // ── Whitespace ────────────────────────────────────────────────────────────

    [Fact]
    public void Whitespace_IsSkipped()
    {
        Kinds("  int   x  ").Should().Equal(TokenKind.Int, TokenKind.Identifier);
    }

    [Fact]
    public void LineComment_IsSkipped()
    {
        Kinds("int x // this is a comment\nint y").Should()
            .Equal(TokenKind.Int, TokenKind.Identifier, TokenKind.Int, TokenKind.Identifier);
    }

    // ── EOF ───────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptySource_ProducesOnlyEof()
    {
        var tokens = Lex("");
        tokens.Should().HaveCount(1);
        tokens[0].Kind.Should().Be(TokenKind.Eof);
    }

    // ── Span accuracy ─────────────────────────────────────────────────────────

    [Fact]
    public void Token_SpanStartsAtColumnOne_ForFirstToken()
    {
        var tok = Lex("x")[0];
        tok.Span.Line.Should().Be(1);
        tok.Span.Column.Should().Be(1);
    }

    [Fact]
    public void Token_SpanTracksLine()
    {
        var tokens = Lex("a\nb");
        var b = tokens.First(t => t.Text == "b");
        b.Span.Line.Should().Be(2);
    }

    // ── Token sequence ────────────────────────────────────────────────────────

    [Fact]
    public void VarDecl_ProducesExpectedSequence()
    {
        Kinds("var x = 42;").Should().Equal(
            TokenKind.Var, TokenKind.Identifier, TokenKind.Eq,
            TokenKind.IntLiteral, TokenKind.Semicolon);
    }

    [Fact]
    public void TypeAnnotatedArrayDecl_ProducesExpectedSequence()
    {
        Kinds("int[] arr = new int[3];").Should().Equal(
            TokenKind.Int, TokenKind.LBracket, TokenKind.RBracket,
            TokenKind.Identifier, TokenKind.Eq,
            TokenKind.New, TokenKind.Int,
            TokenKind.LBracket, TokenKind.IntLiteral, TokenKind.RBracket,
            TokenKind.Semicolon);
    }

    [Fact]
    public void CompoundAssign_IsOneSingleToken()
    {
        // "+=" must be a single PlusEq token, not "+" followed by "="
        Kinds("x += 1").Should().Equal(
            TokenKind.Identifier, TokenKind.PlusEq, TokenKind.IntLiteral);
    }

    [Fact]
    public void Foreach_ProducesCorrectSequence()
    {
        Kinds("foreach (var x in arr)").Should().Equal(
            TokenKind.Foreach, TokenKind.LParen, TokenKind.Var,
            TokenKind.Identifier, TokenKind.In, TokenKind.Identifier,
            TokenKind.RParen);
    }
}
