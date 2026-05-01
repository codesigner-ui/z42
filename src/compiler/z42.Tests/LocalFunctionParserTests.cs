using FluentAssertions;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Parser unit tests for L2 local function declarations.
/// Pairs with archived `add-closures` Requirement R4 and impl spec
/// `LF-1`, `LF-2`. See docs/design/closure.md §3.4.
public sealed class LocalFunctionParserTests
{
    private static CompilationUnit ParseCu(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        return new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
    }

    private static List<Stmt> OuterStmts(string outerBody)
    {
        var src = $$"""
            void Outer() {
                {{outerBody}}
            }
            """;
        return ParseCu(src).Functions[0].Body.Stmts;
    }

    // ── LF-1: AST shape ───────────────────────────────────────────────────────

    [Fact]
    public void LocalFn_ExpressionBody()
    {
        var stmts = OuterStmts("int Helper(int x) => x * 2;");
        var lf = stmts.OfType<LocalFunctionStmt>().Should().ContainSingle().Subject;
        lf.Decl.Name.Should().Be("Helper");
        lf.Decl.Params.Should().HaveCount(1);
        lf.Decl.Params[0].Name.Should().Be("x");
        lf.Decl.Body.Stmts.Should().NotBeEmpty();   // expression body desugared to BlockStmt
    }

    [Fact]
    public void LocalFn_BlockBody()
    {
        var stmts = OuterStmts("int Compute(int x) { return x + 1; }");
        var lf = stmts.OfType<LocalFunctionStmt>().Should().ContainSingle().Subject;
        lf.Decl.Name.Should().Be("Compute");
        lf.Decl.ReturnType.Should().BeOfType<NamedType>().Which.Name.Should().Be("int");
    }

    [Fact]
    public void LocalFn_NoParams()
    {
        var stmts = OuterStmts("int Zero() => 0;");
        var lf = stmts.OfType<LocalFunctionStmt>().Should().ContainSingle().Subject;
        lf.Decl.Params.Should().BeEmpty();
    }

    [Fact]
    public void LocalFn_MultiParam()
    {
        var stmts = OuterStmts("int Add(int a, int b) => a + b;");
        var lf = stmts.OfType<LocalFunctionStmt>().Should().ContainSingle().Subject;
        lf.Decl.Params.Should().HaveCount(2);
    }

    // ── LF-2: disambiguation ──────────────────────────────────────────────────

    [Fact]
    public void VarDecl_NotMisparsedAsLocalFn()
    {
        // `int x = 5;` is a var decl, not a local fn.
        var stmts = OuterStmts("int x = 5;");
        stmts.OfType<LocalFunctionStmt>().Should().BeEmpty();
        stmts.OfType<VarDeclStmt>().Should().HaveCount(1);
    }

    [Fact]
    public void FuncTypeVarDecl_NotMisparsedAsLocalFn()
    {
        // `(int) -> int f = inc;` is a var decl with FuncType annotation, not a local fn.
        var stmts = OuterStmts("(int) -> int f = (int x) => x;");
        stmts.OfType<LocalFunctionStmt>().Should().BeEmpty();
        stmts.OfType<VarDeclStmt>().Should().HaveCount(1);
    }

    [Fact]
    public void LocalFn_WithFuncTypeReturn()
    {
        // Local fn whose return type is itself a function type.
        var stmts = OuterStmts("(int) -> int MakeAdder(int n) => (int x) => x + n;");
        var lf = stmts.OfType<LocalFunctionStmt>().Should().ContainSingle().Subject;
        lf.Decl.Name.Should().Be("MakeAdder");
        lf.Decl.ReturnType.Should().BeOfType<FuncType>();
    }

    [Fact]
    public void TopLevel_FunctionStillWorks()
    {
        // Sanity: ensure the local-fn lookahead does not break top-level parsing.
        var cu = ParseCu("int Square(int x) => x * x;");
        cu.Functions.Should().HaveCount(1);
        cu.Functions[0].Name.Should().Be("Square");
    }
}
