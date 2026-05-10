using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Pipeline;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// impl-dump-ast (2026-05-10) — verifies BoundDumper produces an indented
/// tree shape with `: <type>` annotations on every BoundExpr, exercising the
/// new BoundExprVisitor / BoundStmtVisitor framework end-to-end.
public sealed class BoundDumperTests
{
    private static string DumpBound(string source)
    {
        var tokens = new Lexer(source, "test.z42").Tokenize();
        var diags  = new DiagnosticBag();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu     = parser.ParseCompilationUnit();
        diags.HasErrors.Should().BeFalse();
        var (model, _) = PipelineCore.CheckOnly(cu, DependencyIndex.Empty);
        model.Should().NotBeNull("typecheck must succeed for BoundDumper test source");
        return BoundDumper.Dump(model!);
    }

    [Fact]
    public void Dump_TypedAddition_AnnotatesIntType()
    {
        var src = "int Add(int a, int b) { return a + b; }";

        var output = DumpBound(src);

        output.Should().Contain("Function Add");
        output.Should().Contain("BoundReturn");
        output.Should().Contain("BoundBinary Add : int");
        output.Should().Contain("BoundIdent a : int");
        output.Should().Contain("BoundIdent b : int");
    }

    [Fact]
    public void Dump_StringLiteral_AnnotatesStringType()
    {
        var src = """
            string Hi() {
                return "hello";
            }
            """;

        var output = DumpBound(src);

        output.Should().Contain("BoundLitStr \"hello\" : string");
    }

    [Fact]
    public void Dump_IfElse_RecursesIntoBothBranches()
    {
        var src = """
            int Sign(int x) {
                if (x > 0) { return 1; }
                else { return -1; }
            }
            """;

        var output = DumpBound(src);

        output.Should().Contain("BoundIf");
        output.Should().Contain("Cond:");
        output.Should().Contain("Then:");
        output.Should().Contain("Else:");
        output.Should().Contain("BoundBinary Gt : bool");
        output.Should().Contain("BoundLitInt 1 : int");
    }

    [Fact]
    public void Dump_IndentationIsTwoSpacePerLevel()
    {
        var src = "int F() { return 0; }";

        var output = DumpBound(src);

        // Functions at depth 0; Function entry at depth 1 (2 spaces);
        // BoundBlock at depth 2 (4 spaces); BoundReturn at depth 3 (6 spaces).
        output.Should().Contain("\n  Function ");
        output.Should().Contain("\n    BoundBlock ");
        output.Should().Contain("\n      BoundReturn ");
    }

    [Fact]
    public void Dump_VarDeclWithInit_ShowsInitChild()
    {
        var src = """
            int F() {
                var x = 7;
                return x;
            }
            """;

        var output = DumpBound(src);

        output.Should().Contain("BoundVarDecl x : int");
        output.Should().Contain("Init:");
        output.Should().Contain("BoundLitInt 7 : int");
    }
}
