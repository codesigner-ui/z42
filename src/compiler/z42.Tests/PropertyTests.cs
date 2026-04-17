using FluentAssertions;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Xunit;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Z42.Tests;

/// <summary>
/// Property-based tests using FsCheck random generators + deterministic edge cases.
/// Invariants:
///   - Lexer round-trip: tokenize → join texts → retokenize = same token kinds
///   - Lexer/Parser robustness: never crash on arbitrary input
/// </summary>
public sealed class PropertyTests
{
    // ── FsCheck: Lexer round-trip with random z42 snippets ───────────────────

    [Property(MaxTest = 200, Arbitrary = [typeof(Z42SnippetArb)])]
    public bool Lexer_RoundTrip_Random(Z42Snippet snippet)
    {
        var tokens1 = new Lexer(snippet.Source).Tokenize();
        var kinds1 = tokens1.Where(t => t.Kind != TokenKind.Eof).Select(t => t.Kind).ToList();
        if (kinds1.Count == 0) return true;

        var reconstructed = string.Join(" ",
            tokens1.Where(t => t.Kind != TokenKind.Eof).Select(t => t.Text));
        var tokens2 = new Lexer(reconstructed).Tokenize();
        var kinds2 = tokens2.Where(t => t.Kind != TokenKind.Eof).Select(t => t.Kind).ToList();
        return kinds1.SequenceEqual(kinds2);
    }

    // ── FsCheck: Lexer never crashes on arbitrary strings ────────────────────

    [Property(MaxTest = 500)]
    public bool Lexer_NeverCrashes_Random(NonNull<string> input)
    {
        try { new Lexer(input.Get).Tokenize(); return true; }
        catch { return false; }
    }

    // ── FsCheck: Parser never crashes on arbitrary strings ───────────────────

    [Property(MaxTest = 200)]
    public bool Parser_NeverCrashes_Random(NonNull<string> input)
    {
        try
        {
            var tokens = new Lexer(input.Get).Tokenize();
            new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
            return true;
        }
        catch { return false; }
    }

    // ── Deterministic round-trip tests ───────────────────────────────────────

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
        var reconstructed = string.Join(" ",
            tokens1.Where(t => t.Kind != TokenKind.Eof).Select(t => t.Text));
        var tokens2 = new Lexer(reconstructed).Tokenize();
        var kinds2 = tokens2.Where(t => t.Kind != TokenKind.Eof).Select(t => t.Kind).ToList();
        kinds2.Should().Equal(kinds1);
    }

    // ── Deterministic malformed input tests ──────────────────────────────────

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
    public void Parser_NeverCrashes_OnMalformedInput(string source)
    {
        var act = () =>
        {
            var tokens = new Lexer(source).Tokenize();
            new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void Lexer_NeverCrashes_OnAnySingleChar()
    {
        for (int c = 0; c < 128; c++)
        {
            var act = () => new Lexer(new string((char)c, 1)).Tokenize();
            act.Should().NotThrow();
        }
    }
}

// ── FsCheck custom types + generators ────────────────────────────────────────

/// Wrapper for generated z42-like code snippets.
public record Z42Snippet(string Source)
{
    public override string ToString() => Source;
}

/// FsCheck Arbitrary for Z42Snippet — generates random z42-like token sequences.
public static class Z42SnippetArb
{
    private static readonly string[] Tokens =
    [
        "int", "string", "bool", "var", "return", "if", "else",
        "while", "for", "class", "new", "true", "false", "null", "void",
        "+", "-", "*", "/", "==", "!=", "<", ">", "<=", ">=",
        "&&", "||", "=", ";", ",", "(", ")", "{", "}", "[", "]",
        "x", "y", "z", "foo", "bar", "i", "n", "result",
        "0", "1", "42", "100", "0xFF", "0b1010", "3.14",
    ];

    public static Arbitrary<Z42Snippet> Arbitrary()
    {
        var gen = ArbMap.Default.GeneratorFor<int>().Select(_ =>
        {
            var rnd = Random.Shared;
            int count = rnd.Next(1, 21);
            var picked = new string[count];
            for (int i = 0; i < count; i++)
                picked[i] = Tokens[rnd.Next(Tokens.Length)];
            return new Z42Snippet(string.Join(" ", picked));
        });
        return gen.ToArbitrary();
    }
}
