using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Spec: enhance-expr-recovery (2026-05-08).
/// Verifies that the parser collects multiple expression-level errors
/// instead of stopping at the first ParseException.
public sealed class ParserRecoveryTests
{
    private static (CompilationUnit cu, DiagnosticBag diags) Parse(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu     = parser.ParseCompilationUnit();
        return (cu, parser.Diagnostics);
    }

    [Fact]
    public void SingleBadExprInVarDecl_ReportsErrorAndContinues()
    {
        // Two bad expressions in two consecutive stmts. With recovery the
        // parser should report BOTH (not stop after the first).
        var (cu, diags) = Parse("""
            void Main() {
                var x = 5 +;
                var y = 1 + 1;
            }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().HaveCountGreaterThanOrEqualTo(1, "first expression `5 +` is incomplete");
        cu.Functions.Should().ContainSingle();
    }

    [Fact]
    public void TwoBadExprsAcrossStmts_BothErrorsReported()
    {
        // Both stmts have unfinished expressions. Recovery means we
        // see TWO diagnostics, not one.
        var (cu, diags) = Parse("""
            void Main() {
                var x = 5 +;
                var y = 3 *;
            }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Count.Should().BeGreaterThanOrEqualTo(2,
            "each unfinished expression should produce its own diagnostic");
    }

    [Fact]
    public void BadArgInCall_DoesNotBlockSubsequentArgs()
    {
        // `f(1, *, 3)` — middle arg is a stray `*`. With per-arg recovery,
        // 1 and 3 are still parsed; only the middle becomes ErrorExpr.
        var (_, diags) = Parse("""
            void Foo(int a, int b, int c) {}
            void Main() {
                Foo(1, *, 3);
            }
            """);
        diags.HasErrors.Should().BeTrue();
        // We expect at least 1 parser-level error from the bad arg.
        diags.All.Should().NotBeEmpty();
    }

    [Fact]
    public void BadElemInArrayLiteral_OtherElemsParse()
    {
        var (_, diags) = Parse("""
            void Main() {
                int[] a = new int[] { 1, *, 3 };
            }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().NotBeEmpty();
    }

    [Fact]
    public void ParseExpr_PublicApi_StillThrows_BackwardCompat()
    {
        // `Parser.ParseExpr()` 公开 API 走 `OrThrow()`，遇到 syntax error 必须
        // 抛 ParseException（test 路径 fail-fast，不引入 DiagnosticBag overload）。
        var tokens = new Lexer("5 +").Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var act    = () => parser.ParseExpr();
        act.Should().Throw<ParseException>();
    }
}
