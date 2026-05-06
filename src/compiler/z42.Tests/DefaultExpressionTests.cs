using FluentAssertions;
using Xunit;
using Z42.Core;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// add-default-expression (2026-05-06) — verifies parser produces DefaultExpr,
/// TypeChecker resolves the target type into BoundDefault.Type with E0421 on
/// generic / unknown types, and the IR / VM contract is left unchanged (existing
/// Const* opcodes carry the zero value).
public sealed class DefaultExpressionTests
{
    // ── Parser ────────────────────────────────────────────────────────────────

    private static (CompilationUnit Cu, DiagnosticBag Diags) Parse(string source)
    {
        var diags  = new DiagnosticBag();
        var tokens = new Lexer(source, "test.z42").Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu     = parser.ParseCompilationUnit();
        foreach (var d in parser.Diagnostics.All) diags.Add(d);
        return (cu, diags);
    }

    [Fact]
    public void Parser_DefaultExpr_PrimitiveType()
    {
        var (cu, diags) = Parse("void Main() { var x = default(int); }");
        diags.HasErrors.Should().BeFalse();
        var de = ExtractFirstDefault(cu);
        de.Should().NotBeNull();
        de!.Target.Should().BeOfType<NamedType>().Which.Name.Should().Be("int");
    }

    [Fact]
    public void Parser_DefaultExpr_GenericInstantiated()
    {
        var (cu, diags) = Parse("void Main() { var x = default(List<int>); }");
        diags.HasErrors.Should().BeFalse();
        var de = ExtractFirstDefault(cu);
        de!.Target.Should().BeOfType<GenericType>()
            .Which.Name.Should().Be("List");
    }

    [Fact]
    public void Parser_DefaultExpr_MissingParenIsError()
    {
        var (_, diags) = Parse("void Main() { var x = default int; }");
        diags.HasErrors.Should().BeTrue(
            because: "default without `(` should be a parser error");
    }

    [Fact]
    public void Parser_DefaultExpr_EmptyParensIsError()
    {
        var (_, diags) = Parse("void Main() { var x = default(); }");
        diags.HasErrors.Should().BeTrue(
            because: "default(): TypeExpr required");
    }

    // ── TypeChecker ───────────────────────────────────────────────────────────

    private static (SemanticModel Sem, DiagnosticBag Diags) Bind(string source)
    {
        var diags  = new DiagnosticBag();
        var tokens = new Lexer(source, "test.z42").Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu     = parser.ParseCompilationUnit();
        foreach (var d in parser.Diagnostics.All) diags.Add(d);
        var tc = new TypeChecker(diags, LanguageFeatures.Phase1);
        var sem = tc.Check(cu);
        return (sem, diags);
    }

    [Fact]
    public void TypeChecker_Default_Int_BoundType()
    {
        var (sem, diags) = Bind("void Main() { var x = default(int); }");
        diags.All.Where(d => d.IsError).Should().BeEmpty();
        var bd = ExtractFirstBoundDefault(sem);
        bd.Should().NotBeNull();
        bd!.Type.Should().BeOfType<Z42PrimType>()
            .Which.Name.Should().Be("int");
    }

    [Fact]
    public void TypeChecker_Default_String_BoundType()
    {
        var (sem, diags) = Bind("void Main() { var x = default(string); }");
        diags.All.Where(d => d.IsError).Should().BeEmpty();
        var bd = ExtractFirstBoundDefault(sem);
        bd!.Type.Should().BeOfType<Z42PrimType>()
            .Which.Name.Should().Be("string");
    }

    [Fact]
    public void TypeChecker_Default_UserClass_BoundType()
    {
        var src = @"
            class Foo { public int X; public Foo(int x) { this.X = x; } }
            void Main() { var x = default(Foo); }
        ";
        var (sem, diags) = Bind(src);
        diags.All.Where(d => d.IsError).Should().BeEmpty();
        var bd = ExtractFirstBoundDefault(sem);
        bd!.Type.Should().BeOfType<Z42ClassType>()
            .Which.Name.Should().Be("Foo");
    }

    [Fact]
    public void TypeChecker_Default_UnknownType_RaisesE0421()
    {
        var (_, diags) = Bind("void Main() { var x = default(NoSuchType); }");
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidDefaultType
            && d.Message.Contains("NoSuchType")
            && d.Message.Contains("type not found"));
    }

    [Fact]
    public void TypeChecker_Default_GenericTypeParam_RaisesE0421()
    {
        var src = @"
            class Box<T> { public T Make() { return default(T); } }
            void Main() { var b = new Box<int>(); }
        ";
        var (_, diags) = Bind(src);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidDefaultType
            && d.Message.Contains("generic type parameter"));
    }

    // ── BoundDefault carries the resolved Type ───────────────────────────────

    [Fact]
    public void BoundDefault_TypeIsTarget()
    {
        // The record's primary parameter `Target` and inherited `Type` field
        // must reference the same Z42Type (BoundDefault is its own type witness).
        var (sem, diags) = Bind("void Main() { var x = default(bool); }");
        diags.All.Where(d => d.IsError).Should().BeEmpty();
        var bd = ExtractFirstBoundDefault(sem);
        bd!.Target.Should().BeSameAs(bd.Type,
            because: "BoundDefault Target and Type are the same Z42Type instance");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DefaultExpr? ExtractFirstDefault(CompilationUnit cu)
    {
        foreach (var fn in cu.Functions)
            foreach (var s in fn.Body.Stmts)
                if (s is VarDeclStmt v && v.Init is DefaultExpr de) return de;
        return null;
    }

    private static BoundDefault? ExtractFirstBoundDefault(SemanticModel sem)
    {
        foreach (var body in sem.BoundBodies.Values)
        {
            var found = WalkForDefault(body);
            if (found is not null) return found;
        }
        return null;
    }

    private static BoundDefault? WalkForDefault(BoundBlock block)
    {
        foreach (var s in block.Stmts)
        {
            switch (s)
            {
                case BoundVarDecl v when v.Init is BoundDefault bd:
                    return bd;
                case BoundReturn r when r.Value is BoundDefault bdr:
                    return bdr;
                case BoundBlockStmt bb:
                    if (WalkForDefault(bb.Block) is { } inner) return inner;
                    break;
            }
        }
        return null;
    }
}
