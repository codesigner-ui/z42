using FluentAssertions;
using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.Lowering;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

/// Unit tests for the BoundTree rewriter framework (review.md F2.5 Phase 1,
/// add-bound-tree-rewriter 2026-06-02). Covers:
///
/// 1. Identity rewriters return input nodes by reference (no allocation)
/// 2. A single-node substitution gets the right node replaced and
///    propagates a fresh parent via `with { ... }`
/// 3. Lists short-circuit identity when nothing changed; allocate fresh
///    list only when ≥1 element substituted
/// 4. BoundStmtRewriter composes via the `RewriteExpr` hook
/// 5. Nested rewrites (block inside if inside while) maintain identity
///    short-circuit at every level
public sealed class BoundRewriterTests
{
    private static readonly Span S = new(0, 0, 0, 0);

    // ── Identity rewriter ────────────────────────────────────────────────────

    /// Identity rewriter has no overrides — every record should return by
    /// reference (no `with` allocation).
    private sealed class IdentityExpr : BoundExprRewriter { }

    /// Identity stmt rewriter.
    private sealed class IdentityStmt : BoundStmtRewriter { }

    [Fact]
    public void IdentityExpr_LeafLiteral_ReturnsSameReference()
    {
        var lit = new BoundLitInt(42, Z42Type.Int, S);
        var rewriter = new IdentityExpr();
        var result = rewriter.Visit(lit);
        ReferenceEquals(result, lit).Should().BeTrue("identity rewriter must not allocate");
    }

    [Fact]
    public void IdentityExpr_NestedBinary_ReturnsSameReferences()
    {
        var l = new BoundLitInt(1, Z42Type.Int, S);
        var r = new BoundLitInt(2, Z42Type.Int, S);
        var bin = new BoundBinary(BinaryOp.Add, l, r, Z42Type.Int, S);
        var result = new IdentityExpr().Visit(bin);
        ReferenceEquals(result, bin).Should().BeTrue("nested identity should propagate");
    }

    [Fact]
    public void IdentityExpr_CallWithMultipleArgs_ReturnsSameList()
    {
        var args = new BoundExpr[]
        {
            new BoundLitInt(1, Z42Type.Int, S),
            new BoundLitInt(2, Z42Type.Int, S),
            new BoundLitStr("hi", S),
        };
        var call = new BoundCall(BoundCallKind.Free, null, null, null, "Foo",
                                 args, Z42Type.Int, S);
        var result = (BoundCall)new IdentityExpr().Visit(call);
        ReferenceEquals(result, call).Should().BeTrue("call identity");
        ReferenceEquals(result.Args, call.Args).Should().BeTrue("arg list identity");
    }

    // ── Substitution propagates fresh parents ───────────────────────────────

    /// Replaces every BoundLitInt with value 1 by a BoundLitInt(99).
    private sealed class ReplaceOnesWith99 : BoundExprRewriter
    {
        protected override BoundExpr VisitLitInt(BoundLitInt n) =>
            n.Value == 1 ? new BoundLitInt(99, n.Type, n.Span) : n;
    }

    [Fact]
    public void Substitution_LeafReplaced_ReturnsFreshNode()
    {
        var one = new BoundLitInt(1, Z42Type.Int, S);
        var result = (BoundLitInt)new ReplaceOnesWith99().Visit(one);
        result.Value.Should().Be(99);
        ReferenceEquals(result, one).Should().BeFalse();
    }

    [Fact]
    public void Substitution_PropagatesToParent_FreshBinaryAllocated()
    {
        var l = new BoundLitInt(1, Z42Type.Int, S);   // will be substituted
        var r = new BoundLitInt(2, Z42Type.Int, S);   // identity
        var bin = new BoundBinary(BinaryOp.Add, l, r, Z42Type.Int, S);

        var result = (BoundBinary)new ReplaceOnesWith99().Visit(bin);
        ReferenceEquals(result, bin).Should().BeFalse("parent must be fresh");
        ((BoundLitInt)result.Left).Value.Should().Be(99);
        ReferenceEquals(result.Right, r).Should().BeTrue("untouched right keeps reference");
    }

    [Fact]
    public void Substitution_InsideList_OnlyOneArgReplaced()
    {
        var a = new BoundLitInt(1, Z42Type.Int, S);   // → 99
        var b = new BoundLitInt(2, Z42Type.Int, S);   // identity
        var c = new BoundLitInt(1, Z42Type.Int, S);   // → 99
        var call = new BoundCall(BoundCallKind.Free, null, null, null, "Foo",
                                 new BoundExpr[] { a, b, c }, Z42Type.Int, S);

        var result = (BoundCall)new ReplaceOnesWith99().Visit(call);
        ReferenceEquals(result, call).Should().BeFalse("parent fresh");
        ReferenceEquals(result.Args, call.Args).Should().BeFalse("list fresh");
        result.Args.Should().HaveCount(3);
        ((BoundLitInt)result.Args[0]).Value.Should().Be(99);
        ((BoundLitInt)result.Args[1]).Value.Should().Be(2);
        ReferenceEquals(result.Args[1], b).Should().BeTrue("untouched element keeps reference");
        ((BoundLitInt)result.Args[2]).Value.Should().Be(99);
    }

    // ── BoundStmtRewriter composition via RewriteExpr hook ──────────────────

    /// Stmt rewriter that forwards expression rewriting to a held expr rewriter.
    private sealed class ComposedStmtRewriter : BoundStmtRewriter
    {
        private readonly BoundExprRewriter _exprPass = new ReplaceOnesWith99();
        protected override BoundExpr RewriteExpr(BoundExpr e) => _exprPass.Visit(e);
    }

    [Fact]
    public void IdentityStmt_OnIf_ReturnsSameReference()
    {
        var cond = new BoundLitBool(true, S);
        var thenBlock = new BoundBlock(new List<BoundStmt>(), S);
        var ifStmt = new BoundIf(cond, thenBlock, null, S);
        var result = new IdentityStmt().Visit(ifStmt);
        ReferenceEquals(result, ifStmt).Should().BeTrue();
    }

    [Fact]
    public void ComposedRewriter_ReplacesExprInsideIfCond()
    {
        // if (x == 1) { } ──[ReplaceOnesWith99]──→ if (x == 99) { }
        var x = new BoundIdent("x", Z42Type.Int, S);
        var one = new BoundLitInt(1, Z42Type.Int, S);
        var cond = new BoundBinary(BinaryOp.Eq, x, one, Z42Type.Bool, S);
        var thenBlock = new BoundBlock(new List<BoundStmt>(), S);
        var ifStmt = new BoundIf(cond, thenBlock, null, S);

        var result = (BoundIf)new ComposedStmtRewriter().Visit(ifStmt);
        ReferenceEquals(result, ifStmt).Should().BeFalse("must allocate fresh if");
        var newCond = (BoundBinary)result.Cond;
        ((BoundLitInt)newCond.Right).Value.Should().Be(99);
        // Then block was empty + untouched → still identity
        ReferenceEquals(result.Then, thenBlock).Should().BeTrue();
    }

    [Fact]
    public void RewriteBlock_AllStmtsUnchanged_ReturnsSameBlock()
    {
        var stmt1 = new BoundBreak(S);
        var stmt2 = new BoundContinue(S);
        var block = new BoundBlock(new List<BoundStmt> { stmt1, stmt2 }, S);
        var result = new IdentityStmt().RewriteBlock(block);
        ReferenceEquals(result, block).Should().BeTrue("untouched block returns identity");
        ReferenceEquals(result.Stmts, block.Stmts).Should().BeTrue("stmt list identity");
    }

    [Fact]
    public void RewriteBlock_OneStmtChanges_FreshBlockAllocated()
    {
        // var a = 1;   ──→ var a = 99;
        // break;       ──→ break; (identity)
        var var1 = new BoundVarDecl("a", Z42Type.Int,
                                    new BoundLitInt(1, Z42Type.Int, S), S);
        var br = new BoundBreak(S);
        var block = new BoundBlock(new List<BoundStmt> { var1, br }, S);

        var result = new ComposedStmtRewriter().RewriteBlock(block);
        ReferenceEquals(result, block).Should().BeFalse("fresh block");
        ReferenceEquals(result.Stmts, block.Stmts).Should().BeFalse("fresh list");
        ((BoundLitInt)((BoundVarDecl)result.Stmts[0]).Init!).Value.Should().Be(99);
        ReferenceEquals(result.Stmts[1], br).Should().BeTrue("untouched break keeps ref");
    }

    // ── Edge case: nested swap deep in the tree ─────────────────────────────

    [Fact]
    public void Substitution_DeeplyNested_PropagatesAllTheWayUp()
    {
        // (1 + 2) * (3 + 4)  ──[ReplaceOnesWith99]──→ (99 + 2) * (3 + 4)
        // Only the left sub-tree changes; right sub-tree must keep identity.
        var l1 = new BoundLitInt(1, Z42Type.Int, S);
        var l2 = new BoundLitInt(2, Z42Type.Int, S);
        var r3 = new BoundLitInt(3, Z42Type.Int, S);
        var r4 = new BoundLitInt(4, Z42Type.Int, S);
        var left  = new BoundBinary(BinaryOp.Add, l1, l2, Z42Type.Int, S);
        var right = new BoundBinary(BinaryOp.Add, r3, r4, Z42Type.Int, S);
        var product = new BoundBinary(BinaryOp.Mul, left, right, Z42Type.Int, S);

        var result = (BoundBinary)new ReplaceOnesWith99().Visit(product);
        ReferenceEquals(result, product).Should().BeFalse();
        ReferenceEquals(result.Right, right).Should().BeTrue("untouched right keeps reference");
        var newLeft = (BoundBinary)result.Left;
        ReferenceEquals(newLeft, left).Should().BeFalse("left sub-tree allocated fresh");
        ((BoundLitInt)newLeft.Left).Value.Should().Be(99);
    }
}
