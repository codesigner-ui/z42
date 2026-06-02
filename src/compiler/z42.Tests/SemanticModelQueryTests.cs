using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Unit tests for the SemanticModel query API (review.md F2.3 Phase 1,
/// add-semantic-model-query-api 2026-06-03).
///
/// Covers `GetBoundExpression(Expr)` + `GetExpressionType(Expr)` —
/// looking up the typed bound form of an AST node by reference.
public sealed class SemanticModelQueryTests
{
    /// Run the parser + TypeChecker on a one-function snippet and return
    /// the resulting `SemanticModel`. Aborts the test if any diagnostic
    /// fired — these tests are about query API on a clean bind, not error
    /// paths.
    private static (CompilationUnit Cu, SemanticModel Model) Bind(string snippet)
    {
        var src = $"void Main() {{ {snippet} }}";
        var tokens = new Lexer(src).Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu = parser.ParseCompilationUnit();
        var diags = parser.Diagnostics;
        var model = new TypeChecker(diags).Check(cu, imported: null);
        diags.HasErrors.Should().BeFalse(
            $"snippet `{snippet}` must bind without errors; got {diags.All.Count} diagnostic(s)");
        return (cu, model);
    }

    /// Walk into the single function's first ExprStmt and return the bare
    /// expression node — helper to skip statement wrapping in tests.
    private static Expr SingleExprStmt(CompilationUnit cu)
    {
        var body = cu.Functions[0].Body.Stmts;
        var exprStmt = (ExprStmt)body.Single(s => s is ExprStmt);
        return exprStmt.Expr;
    }

    [Fact]
    public void GetBoundExpression_OnBoundLiteral_ReturnsBoundLitInt()
    {
        // `42` is an ExprStmt whose Expr is a LitIntExpr.
        var (cu, model) = Bind("42;");
        var expr = SingleExprStmt(cu);

        var bound = model.GetBoundExpression(expr);
        bound.Should().BeOfType<BoundLitInt>().Which.Value.Should().Be(42);
    }

    [Fact]
    public void GetExpressionType_OnIntLiteral_IsInt()
    {
        var (cu, model) = Bind("42;");
        var expr = SingleExprStmt(cu);

        model.GetExpressionType(expr).Should().Be(Z42Type.Int);
    }

    [Fact]
    public void GetBoundExpression_OnBinaryExpr_ReturnsBoundBinary()
    {
        // `1 + 2` — BinaryExpr at the root; left + right are LitIntExprs.
        var (cu, model) = Bind("1 + 2;");
        var binExpr = (BinaryExpr)SingleExprStmt(cu);

        var bound = model.GetBoundExpression(binExpr);
        bound.Should().BeOfType<BoundBinary>().Which.Op.Should().Be(BinaryOp.Add);

        // Children are also recorded — each subexpression got its own bind.
        model.GetBoundExpression(binExpr.Left).Should().BeOfType<BoundLitInt>();
        model.GetBoundExpression(binExpr.Right).Should().BeOfType<BoundLitInt>();
    }

    [Fact]
    public void GetBoundExpression_OnUnboundNode_ReturnsNull()
    {
        var (cu, model) = Bind("42;");
        // Fabricate a fresh AST node that TypeChecker never saw.
        var unseen = new LitIntExpr(99, new Z42.Core.Text.Span(0, 0, 0, 0));

        model.GetBoundExpression(unseen).Should().BeNull();
        model.GetExpressionType(unseen).Should().BeNull();
    }

    [Fact]
    public void GetBoundExpression_TwoSameValueLiterals_KeyedByReferenceNotStructure()
    {
        // `1 + 1` — both operands are structurally-equal LitIntExpr(1), but
        // distinct AST node instances. Each gets its own dictionary entry.
        var (cu, model) = Bind("1 + 1;");
        var binExpr = (BinaryExpr)SingleExprStmt(cu);

        binExpr.Left.Should().NotBeSameAs(binExpr.Right,
            "AST nodes at distinct source positions must be distinct instances");

        // Both recorded; both retrievable independently.
        var leftBound  = model.GetBoundExpression(binExpr.Left);
        var rightBound = model.GetBoundExpression(binExpr.Right);
        leftBound.Should().NotBeNull();
        rightBound.Should().NotBeNull();
        // Reference-equality keying means each AST node maps to its own
        // BoundExpr instance (the binder emits a fresh BoundLitInt per call).
        leftBound.Should().NotBeSameAs(rightBound);
    }

    [Fact]
    public void ExpressionBindings_Property_ContainsAllBoundExprs()
    {
        var (_, model) = Bind("var x = 1 + 2; x;");

        // Snippet has at least 4 Expr nodes: 1, 2, (1+2), x — TypeChecker
        // binds each via `BindExpr` → all populate the map.
        model.ExpressionBindings.Count.Should().BeGreaterThanOrEqualTo(4,
            $"got entries: {model.ExpressionBindings.Count}");
    }
}
