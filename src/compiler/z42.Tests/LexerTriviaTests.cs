using FluentAssertions;
using Xunit;
using Z42.Syntax.Lexer;

namespace Z42.Tests;

/// lexer-trivia-preserve (2026-05-10) — verifies Token.LeadingTrivia captures
/// all whitespace + comments preceding each token. Reserved for future
/// formatter / IDE round-trip use; pipelines currently ignore it.
public sealed class LexerTriviaTests
{
    private static List<Token> Lex(string src) => new Lexer(src, "t.z42").Tokenize();

    [Fact]
    public void Trivia_Empty_WhenNoLeadingWhitespace()
    {
        var tokens = Lex("foo");
        tokens[0].Kind.Should().Be(TokenKind.Identifier);
        tokens[0].LeadingTrivia.Should().Be("");
    }

    [Fact]
    public void Trivia_CapturesLeadingSpaces()
    {
        var tokens = Lex("   foo");
        tokens[0].Kind.Should().Be(TokenKind.Identifier);
        tokens[0].LeadingTrivia.Should().Be("   ");
        tokens[0].Text.Should().Be("foo");
    }

    [Fact]
    public void Trivia_CapturesNewlinesAndIndent()
    {
        var tokens = Lex("foo\n  bar");
        tokens[0].LeadingTrivia.Should().Be("");
        tokens[0].Text.Should().Be("foo");
        tokens[1].LeadingTrivia.Should().Be("\n  ");
        tokens[1].Text.Should().Be("bar");
    }

    [Fact]
    public void Trivia_CapturesLineComment()
    {
        var tokens = Lex("// hello\nfoo");
        tokens[0].Text.Should().Be("foo");
        tokens[0].LeadingTrivia.Should().Be("// hello\n");
    }

    [Fact]
    public void Trivia_CapturesBlockComment()
    {
        var tokens = Lex("/* doc */ foo");
        tokens[0].Text.Should().Be("foo");
        tokens[0].LeadingTrivia.Should().Be("/* doc */ ");
    }

    [Fact]
    public void Trivia_CapturesMixedWhitespaceAndComments()
    {
        var tokens = Lex("  // line\n  /* block */  foo");
        tokens[0].Text.Should().Be("foo");
        tokens[0].LeadingTrivia.Should().Be("  // line\n  /* block */  ");
    }

    [Fact]
    public void Trivia_LeadingOnly_NotTrailing()
    {
        // "foo bar" — the space belongs to bar's leading, not foo's trailing.
        var tokens = Lex("foo bar");
        tokens[0].Text.Should().Be("foo");
        tokens[0].LeadingTrivia.Should().Be("");
        tokens[1].Text.Should().Be("bar");
        tokens[1].LeadingTrivia.Should().Be(" ");
    }

    [Fact]
    public void Trivia_TrailingTriviaCarriedByEofToken()
    {
        // Whitespace at end of source belongs to the EOF token's leading.
        var tokens = Lex("foo  ");
        tokens.Last().Kind.Should().Be(TokenKind.Eof);
        tokens.Last().LeadingTrivia.Should().Be("  ");
    }

    [Fact]
    public void Trivia_RoundTrip_ConcatenatesToOriginalSource()
    {
        // Round-trip property: concatenating LeadingTrivia + Text in token order
        // reproduces the original source. The foundation of formatter / round-
        // trip codegen.
        const string src = "  fn /* mid */ Add(int a,\n    int b) {\n  return a + b;  // sum\n}";
        var tokens = Lex(src);
        var rebuilt = string.Concat(tokens.Select(t => t.LeadingTrivia + t.Text));
        rebuilt.Should().Be(src);
    }

    [Fact]
    public void Trivia_AllExistingTokensHaveLeadingTriviaField()
    {
        // Sanity: every token has the field set (never throws on access);
        // empty-string default is acceptable.
        var tokens = Lex("class Foo { int x; }");
        foreach (var t in tokens)
            t.LeadingTrivia.Should().NotBeNull();
    }
}
