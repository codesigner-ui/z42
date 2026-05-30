using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// add-test-timeout-attribute (2026-05-30) — verifies the parser produces the
/// new AttributeArg discriminated union when test-attribute named-arg values
/// are mixed string + integer literals.
public sealed class AttributeArgIntParseTests
{
    private static CompilationUnit ParseCu(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        return new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
    }

    private static (DiagnosticBag Diags, SemanticModel? Sem) Validate(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu = parser.ParseCompilationUnit();
        var diags = parser.Diagnostics;
        SemanticModel? sem = null;
        try { sem = new TypeChecker(diags).Check(cu, imported: null); }
        catch { }
        if (sem is not null) TestAttributeValidator.Validate(cu, sem, diags);
        return (diags, sem);
    }

    private const string ExceptionStub = """
        namespace Std;
        class Exception { public Exception(string msg) {} }
        class SkipSignal : Exception { public SkipSignal(string msg) : base(msg) {} }
        """;

    [Fact]
    public void Parse_MixedStringAndIntArgs_DiscriminatesByType()
    {
        // Even though [Timeout(milliseconds: <int>)] is the canonical int-arg
        // attribute, parser surface accepts any int-valued named-arg on any
        // test-attribute — type checking lives in the validator. We exercise
        // mixed shape on [Skip] purely to confirm parser discrimination.
        const string src = """
            [Test] [Skip(reason: "later", count: 3)] void f() {}
            """;
        var fn = ParseCu(src).Functions.Single();
        var skip = fn.TestAttributes!.Single(a => a.Name == "Skip");
        skip.NamedArgs.Should().NotBeNull();
        skip.NamedArgs!["reason"].Should().BeOfType<AttributeArgString>()
            .Which.Value.Should().Be("later");
        skip.NamedArgs!["count"].Should().BeOfType<AttributeArgInt>()
            .Which.Value.Should().Be(3);
    }

    [Fact]
    public void Parse_NegativeIntLiteral_PreservesSign()
    {
        const string src = """
            [Test] [Skip(reason: "x", offset: -42)] void f() {}
            """;
        var fn = ParseCu(src).Functions.Single();
        var skip = fn.TestAttributes!.Single(a => a.Name == "Skip");
        skip.NamedArgs!["offset"].Should().BeOfType<AttributeArgInt>()
            .Which.Value.Should().Be(-42);
    }

    [Fact]
    public void Parse_UnderscoreSeparatedIntLiteral_RoundTrips()
    {
        const string src = """
            [Test] [Timeout(milliseconds: 60_000)] void slow() {}
            """;
        var fn = ParseCu(src).Functions.Single();
        var timeout = fn.TestAttributes!.Single(a => a.Name == "Timeout");
        timeout.NamedArgs!["milliseconds"].Should().BeOfType<AttributeArgInt>()
            .Which.Value.Should().Be(60_000);
    }

    [Fact]
    public void Validate_SkipReasonAsInt_ReportsE0914()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [Test] [Skip(reason: 42)] void f() {}
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.SkipReasonMissing &&
            d.Message.Contains("must be a string literal"));
    }
}
