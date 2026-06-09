using Z42.Semantics.Bound;

namespace Z42.Semantics.Lowering;

/// <summary>
/// Tree-to-tree rewriter over the <see cref="BoundExpr"/> hierarchy. Each
/// Visit method's default implementation recurses into the node's BoundExpr
/// children and reconstructs the parent only if any child was replaced — so
/// a subclass that overrides nothing acts as identity (returns the input
/// node by reference). Subclasses override the cases they care about.
///
/// Foundation for L3 lowering passes (lambda lifting, async state machine,
/// foreach → while, interpolated string desugar, etc.). review.md F2.5
/// Phase 1 (2026-06-02). Sibling <see cref="BoundStmtRewriter"/> handles
/// statement-shaped trees.
///
/// # Identity short-circuit
///
/// For each interior node, children are visited first. If every visited
/// child is <see cref="object.ReferenceEquals"/> to its original, the
/// rewrite is a no-op for this node — return the input record unchanged
/// so upstream callers also short-circuit. Only when a substitution
/// happens does the visitor allocate a new `with { ... }` record.
///
/// # No exhaustiveness loss
///
/// Inherits from <see cref="BoundExprVisitor{BoundExpr}"/>, so adding a
/// new BoundExpr variant breaks the build until the new abstract method
/// is overridden here — same compile-time exhaustiveness guarantee that
/// motivated the visitor pattern in the first place.
/// </summary>
public abstract class BoundExprRewriter : BoundExprVisitor<BoundExpr>
{
    // ── Literals + identifiers (leaves) ───────────────────────────────────────
    // No BoundExpr children; identity by definition.

    protected override BoundExpr VisitLitInt(BoundLitInt n)         => n;
    protected override BoundExpr VisitLitFloat(BoundLitFloat f)     => f;
    protected override BoundExpr VisitLitStr(BoundLitStr s)         => s;
    protected override BoundExpr VisitLitBool(BoundLitBool b)       => b;
    protected override BoundExpr VisitLitNull(BoundLitNull n)       => n;
    protected override BoundExpr VisitLitChar(BoundLitChar c)       => c;
    protected override BoundExpr VisitDefault(BoundDefault d)       => d;
    protected override BoundExpr VisitTypeof(BoundTypeof t)         => t;
    protected override BoundExpr VisitIdent(BoundIdent id)          => id;
    protected override BoundExpr VisitCapturedIdent(BoundCapturedIdent ci) => ci;
    protected override BoundExpr VisitError(BoundError err)         => err;

    // ── Interpolated string ───────────────────────────────────────────────────

    protected override BoundExpr VisitInterpolatedStr(BoundInterpolatedStr i)
    {
        var newParts = RewriteList(i.Parts, RewriteInterpolationPart);
        return ReferenceEquals(newParts, i.Parts) ? i : i with { Parts = newParts };
    }

    private BoundInterpolationPart RewriteInterpolationPart(BoundInterpolationPart p) => p switch
    {
        BoundExprPart ep => RewriteExprPart(ep),
        BoundTextPart      => p,   // text is a leaf
        _ => p,
    };

    private BoundInterpolationPart RewriteExprPart(BoundExprPart ep)
    {
        var newInner = Visit(ep.Inner);
        return ReferenceEquals(newInner, ep.Inner) ? ep : ep with { Inner = newInner };
    }

    // ── Operators ─────────────────────────────────────────────────────────────

    protected override BoundExpr VisitAssign(BoundAssign a)
    {
        var t = Visit(a.Target);
        var v = Visit(a.Value);
        return ReferenceEquals(t, a.Target) && ReferenceEquals(v, a.Value)
            ? a : a with { Target = t, Value = v };
    }

    protected override BoundExpr VisitBinary(BoundBinary b)
    {
        var l = Visit(b.Left);
        var r = Visit(b.Right);
        return ReferenceEquals(l, b.Left) && ReferenceEquals(r, b.Right)
            ? b : b with { Left = l, Right = r };
    }

    protected override BoundExpr VisitUnary(BoundUnary u)
    {
        var o = Visit(u.Operand);
        return ReferenceEquals(o, u.Operand) ? u : u with { Operand = o };
    }

    protected override BoundExpr VisitPostfix(BoundPostfix p)
    {
        var o = Visit(p.Operand);
        return ReferenceEquals(o, p.Operand) ? p : p with { Operand = o };
    }

    // ── Lambda ────────────────────────────────────────────────────────────────
    //
    // Lambda body is either an Expr (`BoundLambdaExprBody`) or a Block
    // (`BoundLambdaBlockBody`). For Expr body we recurse via Visit; for
    // Block body we offer a virtual `RewriteBlock` hook that subclasses
    // composing with BoundStmtRewriter can override.

    protected override BoundExpr VisitLambda(BoundLambda l)
    {
        var newBody = l.Body switch
        {
            BoundLambdaExprBody eb => RewriteExprLambdaBody(eb),
            BoundLambdaBlockBody bb => RewriteBlockLambdaBody(bb),
            _ => l.Body,
        };
        return ReferenceEquals(newBody, l.Body) ? l : l with { Body = newBody };
    }

    private BoundLambdaBody RewriteExprLambdaBody(BoundLambdaExprBody eb)
    {
        var newExpr = Visit(eb.Expr);
        return ReferenceEquals(newExpr, eb.Expr) ? eb : eb with { Expr = newExpr };
    }

    private BoundLambdaBody RewriteBlockLambdaBody(BoundLambdaBlockBody bb)
    {
        var newBlock = RewriteBlock(bb.Block);
        return ReferenceEquals(newBlock, bb.Block) ? bb : bb with { Block = newBlock };
    }

    /// <summary>
    /// Hook for rewriting a <see cref="BoundBlock"/> nested inside a lambda
    /// body. Default = identity (no stmt rewriting). Subclasses needing
    /// statement-level rewriting override this and forward to a
    /// <see cref="BoundStmtRewriter"/>.
    /// </summary>
    protected virtual BoundBlock RewriteBlock(BoundBlock block) => block;

    // ── Calls ─────────────────────────────────────────────────────────────────

    protected override BoundExpr VisitCall(BoundCall c)
    {
        var newReceiver = c.Receiver != null ? Visit(c.Receiver) : null;
        var newArgs = RewriteList(c.Args, Visit);
        bool receiverChanged = !ReferenceEquals(newReceiver, c.Receiver);
        bool argsChanged = !ReferenceEquals(newArgs, c.Args);
        return (receiverChanged || argsChanged)
            ? c with { Receiver = newReceiver, Args = newArgs }
            : c;
    }

    protected override BoundExpr VisitIndirectCall(BoundIndirectCall ic)
    {
        var newCallee = Visit(ic.Callee);
        var newArgs = RewriteList(ic.Args, Visit);
        return (ReferenceEquals(newCallee, ic.Callee) && ReferenceEquals(newArgs, ic.Args))
            ? ic
            : ic with { Callee = newCallee, Args = newArgs };
    }

    protected override BoundExpr VisitModifiedArg(BoundModifiedArg m)
    {
        var newInner = Visit(m.Inner);
        return ReferenceEquals(newInner, m.Inner) ? m : m with { Inner = newInner };
    }

    // ── Member / index access ─────────────────────────────────────────────────

    protected override BoundExpr VisitMember(BoundMember m)
    {
        var t = Visit(m.Target);
        return ReferenceEquals(t, m.Target) ? m : m with { Target = t };
    }

    protected override BoundExpr VisitIndex(BoundIndex i)
    {
        var t = Visit(i.Target);
        var ix = Visit(i.Index);
        return ReferenceEquals(t, i.Target) && ReferenceEquals(ix, i.Index)
            ? i : i with { Target = t, Index = ix };
    }

    // ── Type operations ───────────────────────────────────────────────────────

    protected override BoundExpr VisitCast(BoundCast c)
    {
        var o = Visit(c.Operand);
        return ReferenceEquals(o, c.Operand) ? c : c with { Operand = o };
    }

    protected override BoundExpr VisitNew(BoundNew n)
    {
        var newArgs = RewriteList(n.Args, Visit);
        return ReferenceEquals(newArgs, n.Args) ? n : n with { Args = newArgs };
    }

    protected override BoundExpr VisitArrayCreate(BoundArrayCreate ac)
    {
        var s = Visit(ac.Size);
        return ReferenceEquals(s, ac.Size) ? ac : ac with { Size = s };
    }

    protected override BoundExpr VisitArrayLit(BoundArrayLit al)
    {
        var newElems = RewriteList(al.Elements, Visit);
        return ReferenceEquals(newElems, al.Elements) ? al : al with { Elements = newElems };
    }

    // ── Control-flow expressions ──────────────────────────────────────────────

    protected override BoundExpr VisitConditional(BoundConditional c)
    {
        var cond  = Visit(c.Cond);
        var then_ = Visit(c.Then);
        var else_ = Visit(c.Else);
        return ReferenceEquals(cond, c.Cond) && ReferenceEquals(then_, c.Then) && ReferenceEquals(else_, c.Else)
            ? c : c with { Cond = cond, Then = then_, Else = else_ };
    }

    protected override BoundExpr VisitNullCoalesce(BoundNullCoalesce nc)
    {
        var l = Visit(nc.Left);
        var r = Visit(nc.Right);
        return ReferenceEquals(l, nc.Left) && ReferenceEquals(r, nc.Right)
            ? nc : nc with { Left = l, Right = r };
    }

    protected override BoundExpr VisitNullConditional(BoundNullConditional nc)
    {
        var t = Visit(nc.Target);
        return ReferenceEquals(t, nc.Target) ? nc : nc with { Target = t };
    }

    protected override BoundExpr VisitIsPattern(BoundIsPattern ip)
    {
        var t = Visit(ip.Target);
        return ReferenceEquals(t, ip.Target) ? ip : ip with { Target = t };
    }

    protected override BoundExpr VisitSwitchExpr(BoundSwitchExpr s)
    {
        var newSubject = Visit(s.Subject);
        var newArms = RewriteList(s.Arms, RewriteSwitchArm);
        return ReferenceEquals(newSubject, s.Subject) && ReferenceEquals(newArms, s.Arms)
            ? s : s with { Subject = newSubject, Arms = newArms };
    }

    private BoundSwitchArm RewriteSwitchArm(BoundSwitchArm arm)
    {
        var newPattern = arm.Pattern != null ? Visit(arm.Pattern) : null;
        var newBody    = Visit(arm.Body);
        return ReferenceEquals(newPattern, arm.Pattern) && ReferenceEquals(newBody, arm.Body)
            ? arm
            : arm with { Pattern = newPattern, Body = newBody };
    }

    // ── List helper ───────────────────────────────────────────────────────────
    //
    // Rewrite each element via `rewrite`. If every result `ReferenceEquals` its
    // input, return the original list reference (so callers can short-circuit
    // via `ReferenceEquals`). Otherwise allocate a fresh `List<T>` with the
    // rewritten elements.

    /// <summary>
    /// Map <paramref name="rewrite"/> across <paramref name="list"/>. Returns
    /// the input list reference (no allocation) when every element is
    /// reference-equal to its original; otherwise returns a new list.
    /// Exposed as <c>internal static</c> so both
    /// <see cref="BoundExprRewriter"/> subclasses and the sibling
    /// <see cref="BoundStmtRewriter"/> can use the identity short-circuit
    /// for any custom list children they introduce.
    /// </summary>
    internal static IReadOnlyList<T> RewriteList<T>(IReadOnlyList<T> list, Func<T, T> rewrite)
        where T : class
    {
        List<T>? newList = null;
        for (int i = 0; i < list.Count; i++)
        {
            var oldItem = list[i];
            var newItem = rewrite(oldItem);
            if (newList == null)
            {
                if (!ReferenceEquals(newItem, oldItem))
                {
                    // First substitution — materialize a copy of the prefix.
                    newList = new List<T>(list.Count);
                    for (int j = 0; j < i; j++) newList.Add(list[j]);
                    newList.Add(newItem);
                }
            }
            else
            {
                newList.Add(newItem);
            }
        }
        return newList ?? list;
    }
}
