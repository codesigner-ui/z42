using FluentAssertions;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Xunit;

namespace Z42.Tests;

/// <summary>
/// Property-style tests for the compiler pipeline.
/// These verify invariants that should hold for ALL valid inputs:
///   - Lexer round-trip: tokenize → join texts → retokenize = same token kinds
///   - Parser robustness: arbitrary input never crashes (only produces diagnostics)
/// </summary>
public sealed class PropertyTests
{
    // ── Lexer round-trip ─────────────────────────────────────────────────────

    /// tokenize(src) → join token.Text → tokenize(joined) should produce
    /// the same sequence of TokenKinds.
    [Theory]
    [InlineData("int x = 42;")]
    [InlineData("string s = \"hello world\";")]
    [InlineData("if (x > 0) { return x; } else { return -x; }")]
    [InlineData("var arr = new int[] { 1, 2, 3 };")]
    [InlineData("class Foo { int x = 0; int Get() { return x; } }")]
    [InlineData("x = a + b * c - d / e % f;")]
    [InlineData("bool ok = (a >= 10) && (b <= 20) || !flag;")]
    [InlineData("for (int i = 0; i < 10; i++) { }")]
    [InlineData("foreach (var item in items) { }")]
    [InlineData("try { } catch { } finally { }")]
    [InlineData("var s = $\"hello {name}, you are {age} years old\";")]
    [InlineData("x?.Foo()?.Bar")]
    [InlineData("a ?? b ?? c")]
    [InlineData("int v = x > 0 ? x : -x;")]
    [InlineData("a <<= 1; b >>= 2; c &= 0xFF; d |= 1; e ^= 3;")]
    [InlineData("0xFF_FF + 0b1010_0101 + 123_456L")]
    [InlineData("switch (x) { case 1: break; default: break; }")]
    public void Lexer_RoundTrip_PreservesTokenKinds(string source)
    {
        var tokens1 = new Lexer(source).Tokenize();
        var kinds1 = tokens1.Where(t => t.Kind != TokenKind.Eof).Select(t => t.Kind).ToList();

        // Reconstruct source from token texts (with spaces between tokens)
        var reconstructed = string.Join(" ",
            tokens1.Where(t => t.Kind != TokenKind.Eof).Select(t => t.Text));

        var tokens2 = new Lexer(reconstructed).Tokenize();
        var kinds2 = tokens2.Where(t => t.Kind != TokenKind.Eof).Select(t => t.Kind).ToList();

        kinds2.Should().Equal(kinds1,
            $"retokenizing the joined token texts should produce the same token kinds");
    }

    // ── Parser robustness: never crash ───────────────────────────────────────

    /// The parser should never throw an unhandled exception for ANY input.
    /// It should either succeed or produce diagnostics.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;;")]
    [InlineData("{{{")]
    [InlineData("}}}")]
    [InlineData("((()))")]
    [InlineData("if if if")]
    [InlineData("class { }")]
    [InlineData("int = ;")]
    [InlineData("fn let mut trait impl")]
    [InlineData("void Main() { return")]
    [InlineData("class Foo { int x = ")]
    [InlineData("var x = 1 +")]
    [InlineData("var x = 1 + * 2;")]
    [InlineData("a.b.c.d.e.f.g.h")]
    [InlineData("\"unterminated string")]
    [InlineData("/* unterminated comment")]
    [InlineData("123abc")]
    [InlineData("0x")]
    [InlineData("0b")]
    [InlineData("...")]
    [InlineData("@#$")]
    public void Parser_NeverCrashes_OnArbitraryInput(string source)
    {
        var act = () =>
        {
            var tokens = new Lexer(source).Tokenize();
            var parser = new Parser(tokens, LanguageFeatures.Phase1);
            parser.ParseCompilationUnit();
        };

        // The parser should either succeed or report diagnostics — never throw
        act.Should().NotThrow(
            $"the parser should handle malformed input gracefully: `{source}`");
    }

    // ── Lexer robustness: never crash ────────────────────────────────────────

    /// The lexer should never throw for any single-character input.
    [Fact]
    public void Lexer_NeverCrashes_OnAnySingleChar()
    {
        for (int c = 0; c < 128; c++)
        {
            var source = new string((char)c, 1);
            var act = () => new Lexer(source).Tokenize();
            act.Should().NotThrow(
                $"lexer should handle char {c} ('{(char)c}') without crashing");
        }
    }

    /// The lexer should never crash on deeply nested interpolated strings.
    [Fact]
    public void Lexer_NeverCrashes_OnNestedInterpolation()
    {
        var source = "$\"outer {$\"inner {42}\"} end\"";
        var act = () => new Lexer(source).Tokenize();
        act.Should().NotThrow();
    }
}
