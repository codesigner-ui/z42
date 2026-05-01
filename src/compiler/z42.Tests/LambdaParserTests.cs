using FluentAssertions;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Parser unit tests for L2 lambda literals + `(T) -> R` function types.
/// Pairs with archived `add-closures` Requirements R1, R2 and impl spec
/// `IR-L1`, `IR-L2`, `IR-L3`. See docs/design/closure.md §3.
public sealed class LambdaParserTests
{
    private static CompilationUnit ParseCu(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        return new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
    }

    private static List<Stmt> ParseStmts(string stmts)
        => ParseCu($"void Main() {{ {stmts} }}").Functions[0].Body.Stmts;

    private static Expr ParseExpr(string expr)
    {
        var v = (VarDeclStmt)ParseStmts($"var _x = {expr};")[0];
        return v.Init!;
    }

    private static TypeExpr? ParseTypeAnnotation(string varDecl)
    {
        var v = (VarDeclStmt)ParseStmts(varDecl)[0];
        return v.TypeAnnotation;
    }

    // ── R1: Lambda literal ────────────────────────────────────────────────────

    [Fact]
    public void Lambda_SingleParam_NoParens()
    {
        var lam = (LambdaExpr)ParseExpr("x => x + 1");
        lam.Params.Should().HaveCount(1);
        lam.Params[0].Name.Should().Be("x");
        lam.Params[0].Type.Should().BeNull();
        lam.Body.Should().BeOfType<LambdaExprBody>();
    }

    [Fact]
    public void Lambda_MultiParam_Untyped()
    {
        var lam = (LambdaExpr)ParseExpr("(x, y) => x * y");
        lam.Params.Should().HaveCount(2);
        lam.Params.Select(p => p.Name).Should().Equal("x", "y");
        lam.Params.All(p => p.Type == null).Should().BeTrue();
    }

    [Fact]
    public void Lambda_NoParam()
    {
        var lam = (LambdaExpr)ParseExpr("() => 42");
        lam.Params.Should().BeEmpty();
        lam.Body.Should().BeOfType<LambdaExprBody>();
    }

    [Fact]
    public void Lambda_ExplicitTypedParams()
    {
        var lam = (LambdaExpr)ParseExpr("(int x, string y) => x");
        lam.Params.Should().HaveCount(2);
        lam.Params[0].Type.Should().BeOfType<NamedType>().Which.Name.Should().Be("int");
        lam.Params[1].Type.Should().BeOfType<NamedType>().Which.Name.Should().Be("string");
    }

    [Fact]
    public void Lambda_BlockBody()
    {
        var lam = (LambdaExpr)ParseExpr("x => { return x + 1; }");
        lam.Body.Should().BeOfType<LambdaBlockBody>()
            .Which.Block.Stmts.Should().NotBeEmpty();
    }

    // ── R1+IR-L3: paren / lambda disambiguation ─────────────────────────────

    [Fact]
    public void ParenExpr_IsNotLambda()
    {
        // `(x + 1)` is a paren expression, not a lambda.
        var expr = ParseExpr("(1 + 2) * 3");
        expr.Should().BeOfType<BinaryExpr>();
    }

    [Fact]
    public void Cast_IsNotLambda()
    {
        // `(int)expr` is a cast, not a lambda.
        var expr = ParseExpr("(int)x");
        expr.Should().BeOfType<CastExpr>();
    }

    // ── R2: Function type `(T) -> R` ──────────────────────────────────────────

    [Fact]
    public void FuncType_SingleParam()
    {
        var ty = (FuncType)ParseTypeAnnotation("(int) -> int f = inc;")!;
        ty.ParamTypes.Should().HaveCount(1);
        ty.ParamTypes[0].Should().BeOfType<NamedType>().Which.Name.Should().Be("int");
        ty.ReturnType.Should().BeOfType<NamedType>().Which.Name.Should().Be("int");
    }

    [Fact]
    public void FuncType_MultiParam()
    {
        var ty = (FuncType)ParseTypeAnnotation("(int, string) -> bool f = inc;")!;
        ty.ParamTypes.Should().HaveCount(2);
        ty.ParamTypes[0].Should().BeOfType<NamedType>().Which.Name.Should().Be("int");
        ty.ParamTypes[1].Should().BeOfType<NamedType>().Which.Name.Should().Be("string");
        ty.ReturnType.Should().BeOfType<NamedType>().Which.Name.Should().Be("bool");
    }

    [Fact]
    public void FuncType_VoidReturn()
    {
        var ty = (FuncType)ParseTypeAnnotation("(int) -> void f = inc;")!;
        ty.ReturnType.Should().BeOfType<VoidType>();
    }

    [Fact]
    public void FuncType_NoParams()
    {
        var ty = (FuncType)ParseTypeAnnotation("() -> int f = inc;")!;
        ty.ParamTypes.Should().BeEmpty();
        ty.ReturnType.Should().BeOfType<NamedType>().Which.Name.Should().Be("int");
    }

    [Fact]
    public void FuncType_HigherOrder()
    {
        // `((int) -> int) -> int` — function whose argument is itself a function.
        var ty = (FuncType)ParseTypeAnnotation("((int) -> int) -> int f = inc;")!;
        ty.ParamTypes.Should().HaveCount(1);
        ty.ParamTypes[0].Should().BeOfType<FuncType>();
    }

    [Fact]
    public void FuncType_AsGenericArg()
    {
        // `List<(int) -> bool>` — function type as a generic argument.
        var ty = (GenericType)ParseTypeAnnotation("List<(int) -> bool> fs = xs;")!;
        ty.Name.Should().Be("List");
        ty.TypeArgs[0].Should().BeOfType<FuncType>();
    }
}
