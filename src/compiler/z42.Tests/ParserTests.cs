using FluentAssertions;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Unit tests for the Parser: verify that specific constructs produce the
/// correct AST nodes without going through the full compile pipeline.
public sealed class ParserTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CompilationUnit ParseCu(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        return new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
    }

    /// Parse a snippet that lives inside `void Main() { <stmts> }`.
    private static List<Stmt> ParseStmts(string stmts)
        => ParseCu($"void Main() {{ {stmts} }}").Functions[0].Body.Stmts;

    /// Parse a single statement.
    private static Stmt ParseStmt(string stmt)
        => ParseStmts(stmt).Should().ContainSingle().Subject;

    /// Parse a single expression via a var-decl wrapper.
    private static Expr ParseExpr(string expr)
    {
        var v = (VarDeclStmt)ParseStmt($"var _x = {expr};");
        return v.Init!;
    }

    // ── var declaration ───────────────────────────────────────────────────────

    [Fact]
    public void VarDecl_WithInit()
    {
        var stmt = (VarDeclStmt)ParseStmt("var x = 5;");
        stmt.Name.Should().Be("x");
        stmt.TypeAnnotation.Should().BeNull();
        stmt.Init.Should().BeOfType<LitIntExpr>()
            .Which.Value.Should().Be(5);
    }

    [Fact]
    public void VarDecl_WithoutInit()
    {
        var stmt = (VarDeclStmt)ParseStmt("var x;");
        stmt.Name.Should().Be("x");
        stmt.Init.Should().BeNull();
    }

    // ── Type-annotated declaration ────────────────────────────────────────────

    [Fact]
    public void TypeAnnotatedDecl_Int()
    {
        var stmt = (VarDeclStmt)ParseStmt("int x = 42;");
        stmt.TypeAnnotation.Should().BeOfType<NamedType>()
            .Which.Name.Should().Be("int");
        stmt.Init.Should().BeOfType<LitIntExpr>()
            .Which.Value.Should().Be(42);
    }

    [Fact]
    public void TypeAnnotatedDecl_String()
    {
        var stmt = (VarDeclStmt)ParseStmt("string s = \"hello\";");
        stmt.TypeAnnotation.Should().BeOfType<NamedType>()
            .Which.Name.Should().Be("string");
    }

    [Fact]
    public void TypeAnnotatedDecl_ArrayType()
    {
        var stmt = (VarDeclStmt)ParseStmt("int[] arr = new int[3];");
        stmt.TypeAnnotation.Should().BeOfType<ArrayType>()
            .Which.Element.Should().BeOfType<NamedType>()
            .Which.Name.Should().Be("int");
        stmt.Init.Should().BeOfType<ArrayCreateExpr>();
    }

    // ── Array expressions ─────────────────────────────────────────────────────

    [Fact]
    public void ArrayCreate_ParsedCorrectly()
    {
        var expr = (ArrayCreateExpr)ParseExpr("new int[5]");
        expr.ElemType.Should().BeOfType<NamedType>()
            .Which.Name.Should().Be("int");
        expr.Size.Should().BeOfType<LitIntExpr>()
            .Which.Value.Should().Be(5);
    }

    [Fact]
    public void ArrayLiteral_ParsedCorrectly()
    {
        var expr = (ArrayLitExpr)ParseExpr("new int[] { 1, 2, 3 }");
        expr.ElemType.Should().BeOfType<NamedType>()
            .Which.Name.Should().Be("int");
        expr.Elements.Should().HaveCount(3);
        expr.Elements[0].Should().BeOfType<LitIntExpr>().Which.Value.Should().Be(1);
        expr.Elements[2].Should().BeOfType<LitIntExpr>().Which.Value.Should().Be(3);
    }

    [Fact]
    public void ArrayIndex_ParsedCorrectly()
    {
        var expr = (IndexExpr)ParseExpr("arr[2]");
        expr.Target.Should().BeOfType<IdentExpr>().Which.Name.Should().Be("arr");
        expr.Index.Should().BeOfType<LitIntExpr>().Which.Value.Should().Be(2);
    }

    // ── String builtins ───────────────────────────────────────────────────────

    [Fact]
    public void StringLength_IsAMemberExpr()
    {
        var expr = (MemberExpr)ParseExpr("s.Length");
        expr.Member.Should().Be("Length");
        expr.Target.Should().BeOfType<IdentExpr>().Which.Name.Should().Be("s");
    }

    [Fact]
    public void StringContains_IsACallOnMemberExpr()
    {
        var expr = (CallExpr)ParseExpr("s.Contains(\"x\")");
        var callee = expr.Callee.Should().BeOfType<MemberExpr>().Subject;
        callee.Member.Should().Be("Contains");
        expr.Args.Should().HaveCount(1);
    }

    [Fact]
    public void StringSubstring_TwoArgs()
    {
        var expr = (CallExpr)ParseExpr("s.Substring(7, 5)");
        expr.Args.Should().HaveCount(2);
    }

    // ── Control flow statements ───────────────────────────────────────────────

    [Fact]
    public void IfStmt_WithBraces()
    {
        var stmt = (IfStmt)ParseStmt("if (x > 0) { return; }");
        stmt.Condition.Should().BeOfType<BinaryExpr>().Which.Op.Should().Be(">");
        stmt.Then.Stmts.Should().ContainSingle().Which.Should().BeOfType<ReturnStmt>();
        stmt.Else.Should().BeNull();
    }

    [Fact]
    public void IfStmt_BraceFree()
    {
        var stmt = (IfStmt)ParseStmt("if (x > 0) return;");
        stmt.Then.Stmts.Should().ContainSingle().Which.Should().BeOfType<ReturnStmt>();
    }

    [Fact]
    public void IfElse()
    {
        var stmt = (IfStmt)ParseStmt("if (x) { } else { }");
        stmt.Else.Should().NotBeNull();
    }

    [Fact]
    public void ElseIf_Chain()
    {
        var stmt = (IfStmt)ParseStmt("if (a) { } else if (b) { } else { }");
        stmt.Else.Should().BeOfType<IfStmt>()
            .Which.Else.Should().BeOfType<BlockStmt>();
    }

    [Fact]
    public void WhileStmt_ParsedCorrectly()
    {
        var stmt = (WhileStmt)ParseStmt("while (i < 10) { i++; }");
        stmt.Condition.Should().BeOfType<BinaryExpr>().Which.Op.Should().Be("<");
        stmt.Body.Stmts.Should().ContainSingle();
    }

    [Fact]
    public void ForStmt_WithAllParts()
    {
        var stmt = (ForStmt)ParseStmt("for (var i = 0; i < 5; i++) { }");
        stmt.Init.Should().BeOfType<VarDeclStmt>();
        stmt.Condition.Should().BeOfType<BinaryExpr>();
        stmt.Increment.Should().BeOfType<PostfixExpr>()
            .Which.Op.Should().Be("++");
    }

    [Fact]
    public void ForeachStmt_ParsedCorrectly()
    {
        var stmt = (ForeachStmt)ParseStmt("foreach (var x in arr) { }");
        stmt.VarName.Should().Be("x");
        stmt.Collection.Should().BeOfType<IdentExpr>().Which.Name.Should().Be("arr");
    }

    [Fact]
    public void BreakStmt_ParsedCorrectly()
    {
        ParseStmt("break;").Should().BeOfType<BreakStmt>();
    }

    [Fact]
    public void ContinueStmt_ParsedCorrectly()
    {
        ParseStmt("continue;").Should().BeOfType<ContinueStmt>();
    }

    // ── Compound assignment ───────────────────────────────────────────────────

    [Theory]
    [InlineData("x += 1;", "+")]
    [InlineData("x -= 1;", "-")]
    [InlineData("x *= 2;", "*")]
    [InlineData("x /= 2;", "/")]
    [InlineData("x %= 3;", "%")]
    public void CompoundAssign_DesugarsToBinaryExpr(string src, string op)
    {
        var stmt = (ExprStmt)ParseStmt(src);
        var assign = stmt.Expr.Should().BeOfType<AssignExpr>().Subject;
        assign.Target.Should().BeOfType<IdentExpr>().Which.Name.Should().Be("x");
        var rhs = assign.Value.Should().BeOfType<BinaryExpr>().Subject;
        rhs.Op.Should().Be(op);
    }

    // ── Expressions ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1 + 2",  "+")]
    [InlineData("3 - 1",  "-")]
    [InlineData("4 * 5",  "*")]
    [InlineData("8 / 2",  "/")]
    [InlineData("9 % 3",  "%")]
    public void BinaryArith_ParsedCorrectly(string src, string op)
    {
        var expr = (BinaryExpr)ParseExpr(src);
        expr.Op.Should().Be(op);
    }

    [Theory]
    [InlineData("a == b",  "==")]
    [InlineData("a != b",  "!=")]
    [InlineData("a < b",   "<")]
    [InlineData("a <= b",  "<=")]
    [InlineData("a > b",   ">")]
    [InlineData("a >= b",  ">=")]
    public void BinaryComparison_ParsedCorrectly(string src, string op)
    {
        var expr = (BinaryExpr)ParseExpr(src);
        expr.Op.Should().Be(op);
    }

    [Fact]
    public void LogicalAnd_ParsedCorrectly()
    {
        ((BinaryExpr)ParseExpr("a && b")).Op.Should().Be("&&");
    }

    [Fact]
    public void LogicalOr_ParsedCorrectly()
    {
        ((BinaryExpr)ParseExpr("a || b")).Op.Should().Be("||");
    }

    [Fact]
    public void UnaryNot_ParsedCorrectly()
    {
        var expr = (UnaryExpr)ParseExpr("!x");
        expr.Op.Should().Be("!");
    }

    [Fact]
    public void UnaryNeg_ParsedCorrectly()
    {
        var expr = (UnaryExpr)ParseExpr("-x");
        expr.Op.Should().Be("-");
    }

    [Fact]
    public void PostfixInc_ParsedCorrectly()
    {
        var expr = (PostfixExpr)ParseExpr("x++");
        expr.Op.Should().Be("++");
    }

    [Fact]
    public void PostfixDec_ParsedCorrectly()
    {
        var expr = (PostfixExpr)ParseExpr("x--");
        expr.Op.Should().Be("--");
    }

    [Fact]
    public void PrecedenceIsCorrect_MulBeforeAdd()
    {
        // 1 + 2 * 3  →  Add(1, Mul(2, 3))
        var expr = (BinaryExpr)ParseExpr("1 + 2 * 3");
        expr.Op.Should().Be("+");
        expr.Right.Should().BeOfType<BinaryExpr>().Which.Op.Should().Be("*");
    }

    [Fact]
    public void TernaryExpr_ParsedCorrectly()
    {
        var expr = (ConditionalExpr)ParseExpr("x > 0 ? 1 : -1");
        expr.Cond.Should().BeOfType<BinaryExpr>().Which.Op.Should().Be(">");
        expr.Then.Should().BeOfType<LitIntExpr>().Which.Value.Should().Be(1);
    }

    // ── Literals ──────────────────────────────────────────────────────────────

    [Fact]
    public void IntLiteral_ParsedCorrectly()
    {
        ((LitIntExpr)ParseExpr("123")).Value.Should().Be(123);
    }

    [Fact]
    public void BoolTrue_ParsedCorrectly()
    {
        ((LitBoolExpr)ParseExpr("true")).Value.Should().BeTrue();
    }

    [Fact]
    public void BoolFalse_ParsedCorrectly()
    {
        ((LitBoolExpr)ParseExpr("false")).Value.Should().BeFalse();
    }

    [Fact]
    public void StringLiteral_ParsedCorrectly()
    {
        ((LitStrExpr)ParseExpr("\"hello\"")).Value.Should().Be("hello");
    }

    [Fact]
    public void NullLiteral_ParsedCorrectly()
    {
        ParseExpr("null").Should().BeOfType<LitNullExpr>();
    }

    // ── Function declarations ─────────────────────────────────────────────────

    [Fact]
    public void FunctionDecl_WithParams()
    {
        var cu = ParseCu("int Add(int a, int b) { return a + b; }");
        var fn = cu.Functions.Should().ContainSingle().Subject;
        fn.Name.Should().Be("Add");
        fn.Params.Should().HaveCount(2);
        fn.Params[0].Name.Should().Be("a");
        fn.ReturnType.Should().BeOfType<NamedType>().Which.Name.Should().Be("int");
    }

    [Fact]
    public void FunctionDecl_VoidNoParams()
    {
        var fn = ParseCu("void Main() { }").Functions.Should().ContainSingle().Subject;
        fn.Name.Should().Be("Main");
        fn.Params.Should().BeEmpty();
        fn.ReturnType.Should().BeOfType<VoidType>();
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public void MissingClosingBrace_ReportsDiagnostic()
    {
        var tokens = new Lexer("void Main() {").Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        parser.ParseCompilationUnit();
        parser.Diagnostics.HasErrors.Should().BeTrue();
        parser.Diagnostics.All.Should().ContainSingle()
            .Which.Message.Should().Contain("expected `}`");
    }

    [Fact]
    public void MissingSemicolon_AllowedInSomeExprStmts()
    {
        // Expression statements tolerate missing semicolons
        var stmts = ParseStmts("x = 1");
        stmts.Should().ContainSingle();
    }

    [Fact]
    public void UnknownTokenInExpr_ReportsDiagnostic()
    {
        var tokens = new Lexer("void Main() { var x = @; }").Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        parser.ParseCompilationUnit();
        parser.Diagnostics.HasErrors.Should().BeTrue();
    }

    // ── extern / [Native] ─────────────────────────────────────────────────────

    [Fact]
    public void ExternMethod_ParsesIsExternAndNativeIntrinsic()
    {
        var cu = ParseCu("""
            class Console {
                [Native("__println")]
                public static extern void WriteLine(string value);
            }
            """);
        var m = cu.Classes.Should().ContainSingle().Subject
                  .Methods.Should().ContainSingle().Subject;
        m.IsExtern.Should().BeTrue();
        m.NativeIntrinsic.Should().Be("__println");
        m.Params.Should().ContainSingle().Which.Name.Should().Be("value");
    }

    [Fact]
    public void ExternMethod_NoBody_SemicolonOnly()
    {
        var cu = ParseCu("""
            class Foo {
                [Native("__bar")]
                public static extern void Bar();
            }
            """);
        var m = cu.Classes[0].Methods[0];
        m.IsExtern.Should().BeTrue();
        m.Body.Stmts.Should().BeEmpty();
    }

    [Fact]
    public void ExternMethod_MultipleParams()
    {
        var cu = ParseCu("""
            class Str {
                [Native("__str_substring")]
                public static extern string Substring(string s, int start, int length);
            }
            """);
        var m = cu.Classes[0].Methods[0];
        m.IsExtern.Should().BeTrue();
        m.NativeIntrinsic.Should().Be("__str_substring");
        m.Params.Should().HaveCount(3);
    }

    [Fact]
    public void RegularMethod_IsExternFalse()
    {
        var fn = ParseCu("void Main() { }").Functions.Should().ContainSingle().Subject;
        fn.IsExtern.Should().BeFalse();
        fn.NativeIntrinsic.Should().BeNull();
    }

    // ── L3-G2.5 extern impl (user classes) ───────────────────────────────────

    [Fact]
    public void ImplDecl_Basic()
    {
        var cu = ParseCu(@"
interface IGreet { string Hello(); }
class Foo { int x; }
impl IGreet for Foo {
    public string Hello() { return ""hi""; }
}
void Main() { }");
        cu.Impls.Should().HaveCount(1);
        var impl = cu.Impls[0];
        ((NamedType)impl.TraitType).Name.Should().Be("IGreet");
        ((NamedType)impl.TargetType).Name.Should().Be("Foo");
        impl.Methods.Should().ContainSingle().Subject.Name.Should().Be("Hello");
    }

    [Fact]
    public void ImplDecl_GenericTraitArg()
    {
        var cu = ParseCu(@"
interface IEq<T> { bool Equals(T other); }
class Foo { int x; }
impl IEq<int> for Foo {
    public bool Equals(int other) { return false; }
}
void Main() { }");
        var impl = cu.Impls.Should().ContainSingle().Subject;
        impl.TraitType.Should().BeOfType<GenericType>();
        ((GenericType)impl.TraitType).Name.Should().Be("IEq");
        ((GenericType)impl.TraitType).TypeArgs.Should().ContainSingle();
    }

    [Fact]
    public void ImplDecl_MultipleMethods()
    {
        var cu = ParseCu(@"
interface IPair { int First(); int Second(); }
class P { int a; int b; }
impl IPair for P {
    public int First()  { return 1; }
    public int Second() { return 2; }
}
void Main() { }");
        var impl = cu.Impls.Should().ContainSingle().Subject;
        impl.Methods.Should().HaveCount(2);
        impl.Methods.Select(m => m.Name).Should().Equal("First", "Second");
    }
}
