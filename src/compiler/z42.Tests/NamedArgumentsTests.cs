using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// <summary>
/// Coverage for spec add-named-arguments (2026-05-11). Parser-level naming
/// recognition + TypeChecker reorder for the two wired call paths
/// (static class methods + constructors). Imported / other paths emit
/// Z1002 fallback — covered by FallbackForUnknownParamNames cases.
/// </summary>
public sealed class NamedArgumentsTests
{
    private static (CompilationUnit cu, DiagnosticBag diags) ParseSource(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        var diags  = new DiagnosticBag();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        return (cu, diags);
    }

    private static (CompilationUnit cu, DiagnosticBag diags) Compile(string source)
    {
        var (cu, parseDiags) = ParseSource(source);
        var tc = new TypeChecker(parseDiags);
        tc.Check(cu);
        return (cu, parseDiags);
    }

    // ── Parser-level shape (basic naming) ───────────────────────────────────

    [Fact]
    public void Parse_SimpleNamedArg_ProducesArgumentWithName()
    {
        var (cu, _) = ParseSource("void Main() { Greet(name: \"Alice\"); }");
        var call = (CallExpr)((ExprStmt)cu.Functions[0].Body!.Stmts[0]).Expr;
        call.Args.Should().HaveCount(1);
        call.Args[0].Name.Should().Be("name");
        call.Args[0].Value.Should().BeOfType<LitStrExpr>();
    }

    [Fact]
    public void Parse_NamedArgWithRef_ProducesArgWithModifier()
    {
        var (cu, _) = ParseSource("void Main() { Update(target: ref x); }");
        var call = (CallExpr)((ExprStmt)cu.Functions[0].Body!.Stmts[0]).Expr;
        call.Args[0].Name.Should().Be("target");
        call.Args[0].Value.Should().BeOfType<ModifiedArg>()
            .Which.Modifier.Should().Be(ArgModifier.Ref);
    }

    [Fact]
    public void Parse_TernaryInPositionalArg_NotConfusedWithNamed()
    {
        var (cu, _) = ParseSource("void Main() { f(a ? b : c); }");
        var call = (CallExpr)((ExprStmt)cu.Functions[0].Body!.Stmts[0]).Expr;
        call.Args[0].Name.Should().BeNull();
        call.Args[0].Value.Should().BeOfType<ConditionalExpr>();
    }

    // ── Z1001 positional-after-named ────────────────────────────────────────

    [Fact]
    public void TypeCheck_PositionalAfterNamed_EmitsZ1001()
    {
        var (_, diags) = Compile("""
            void Greet(string name, int n) { }
            void Main() { Greet(name: "Alice", 1); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.PositionalAfterNamed);
    }

    // ── Z1002 unknown name ──────────────────────────────────────────────────

    [Fact]
    public void TypeCheck_UnknownArgumentName_OnStaticCall_EmitsZ1002()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static void Run(int width) { }
            }
            void Main() { Tool.Run(unknownName: 5); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.UnknownArgumentName);
    }

    // ── Z1003 duplicate name ────────────────────────────────────────────────

    [Fact]
    public void TypeCheck_DuplicateArgumentName_EmitsZ1003()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static void Draw(string color, int width) { }
            }
            void Main() { Tool.Draw(color: "red", color: "blue"); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.DuplicateArgumentName);
    }

    // ── Z1004 doubly specified ──────────────────────────────────────────────

    [Fact]
    public void TypeCheck_ParameterDoublySpecified_EmitsZ1004()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static void Draw(string color, int width = 1) { }
            }
            void Main() { Tool.Draw("red", color: "blue"); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.ParameterDoublySpecified);
    }

    // ── Z1005 missing required ──────────────────────────────────────────────

    [Fact]
    public void TypeCheck_MissingRequired_AfterNamed_EmitsZ1005()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static void Draw(string color, int width) { }
            }
            void Main() { Tool.Draw(width: 2); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.MissingRequiredArgument);
    }

    // ── Reorder works: out-of-order named ───────────────────────────────────

    [Fact]
    public void TypeCheck_OutOfOrderNamedOnStaticCall_BindsClean()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static void Draw(string color, int width, bool filled) { }
            }
            void Main() { Tool.Draw(filled: true, color: "red", width: 2); }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    // ── Skip middle default with named ──────────────────────────────────────

    [Fact]
    public void TypeCheck_SkipMiddleDefault_WithNamedTail_BindsClean()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static int M(int a, int b = 10, int c = 20) { return a + b + c; }
            }
            void Main() { var r = Tool.M(1, c: 30); }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    // ── Constructor named args ──────────────────────────────────────────────

    [Fact]
    public void TypeCheck_ConstructorNamedArgs_BindsClean()
    {
        var (_, diags) = Compile("""
            class Box {
                public int x;
                public int y;
                public Box(int width, int height) { x = width; y = height; }
            }
            void Main() { var b = new Box(height: 3, width: 5); }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void TypeCheck_ConstructorUnknownName_EmitsZ1002()
    {
        var (_, diags) = Compile("""
            class Box {
                public Box(int width, int height) { }
            }
            void Main() { var b = new Box(width: 5, depth: 3); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.UnknownArgumentName);
    }
}
