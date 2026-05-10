using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Pipeline;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// impl-dump-ast (2026-05-10) — verifies AstDumper produces an indented tree
/// shape (not the C# default record `ToString()` single-liner) and that the
/// salient attributes / spans / structure flow into the output.
public sealed class AstDumperTests
{
    private static string Dump(string source)
    {
        var tokens = new Lexer(source, "test.z42").Tokenize();
        var diags  = new DiagnosticBag();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu     = parser.ParseCompilationUnit();
        diags.HasErrors.Should().BeFalse(parser.Diagnostics.All
            .Aggregate("", (a, d) => $"{a}\n{d}"));
        return AstDumper.Dump(cu);
    }

    [Fact]
    public void Dump_Addition_PrintsBinaryExprTree()
    {
        var src = "int Add(int a, int b) { return a + b; }";

        var output = Dump(src);

        output.Should().Contain("CompilationUnit");
        output.Should().Contain("FunctionDecl");
        output.Should().Contain("Add");
        output.Should().Contain("BinaryExpr +");
        output.Should().Contain("Left:");
        output.Should().Contain("Right:");
        output.Should().Contain("IdentExpr a");
        output.Should().Contain("IdentExpr b");
    }

    [Fact]
    public void Dump_Indentation_IsTwoSpacePerLevel()
    {
        var src = "int F() { return 1; }";

        var output = Dump(src);

        // CompilationUnit at depth 0; "Functions [..]" at depth 1 (2 spaces);
        // FunctionDecl at depth 2 (4 spaces); Body block at depth 3 (6 spaces).
        output.Should().Contain("\n  Functions [");          // 2-space
        output.Should().Contain("\n    FunctionDecl ");      // 4-space
        output.Should().Contain("\n      Body:");            // 6-space
    }

    [Fact]
    public void Dump_Literals_RenderValueAndType()
    {
        var src = """
            int Vals() {
                var s = "hi";
                var i = 42;
                var b = true;
                return i;
            }
            """;

        var output = Dump(src);

        output.Should().Contain("LitStrExpr \"hi\"");
        output.Should().Contain("LitIntExpr 42");
        output.Should().Contain("LitBoolExpr True");
    }

    [Fact]
    public void Dump_NestedIfElse_RendersBranchLabels()
    {
        var src = """
            int Sign(int x) {
                if (x > 0) { return 1; }
                else { return -1; }
            }
            """;

        var output = Dump(src);

        output.Should().Contain("IfStmt");
        output.Should().Contain("Cond:");
        output.Should().Contain("Then:");
        output.Should().Contain("Else:");
        output.Should().Contain("BinaryExpr >");
    }

    [Fact]
    public void Dump_NotDefaultRecordToString()
    {
        // Sanity: the legacy `Console.WriteLine(cu)` produced a single-line
        // record dump like `CompilationUnit { Namespace = , ... }`. Our output
        // must be multi-line, indented, and use our own format.
        var src = "int F() { return 0; }";

        var output = Dump(src);

        output.Split('\n').Length.Should().BeGreaterThan(5,
            "indented tree must span multiple lines");
        output.Should().NotContain("CompilationUnit { ",
            "must not match the legacy default record ToString() format");
    }

    [Fact]
    public void Dump_ClassWithFields_RecursesIntoFieldDecls()
    {
        var src = """
            class Point {
                int X;
                int Y;
            }
            """;

        var output = Dump(src);

        output.Should().Contain("ClassDecl class Point");
        output.Should().Contain("Fields [2 items]:");
        output.Should().Contain("FieldDecl ");
        output.Should().Contain("X");
        output.Should().Contain("Y");
    }
}
