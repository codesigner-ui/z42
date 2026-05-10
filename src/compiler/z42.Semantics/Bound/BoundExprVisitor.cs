namespace Z42.Semantics.Bound;

/// Void-substitute for generic visitors that don't return a value
/// (.NET disallows `void` as a generic type argument).
public readonly record struct Unit;

/// Type-driven dispatcher over the BoundExpr hierarchy.
///
/// 引入动机（review §2.1）：把"加新 BoundExpr 节点要改 5–15 处 switch"压缩为
/// "改基类 switch + 加 abstract 方法 → 所有子类编译期失败 → 强制 override"。
///
/// 新增节点流程：
///   1. 在 BoundExpr.cs 加 `sealed record BoundXxx(...)`
///   2. 在 `Visit(BoundExpr)` switch 加一行 `BoundXxx x => VisitXxx(x)`
///   3. 加 `protected abstract TResult VisitXxx(BoundXxx x)`
///   4. build → 所有 BoundExprVisitor 子类编译失败提示需要实现 VisitXxx
///   5. 在每个子类（含 Walker 默认）实现新方法
public abstract class BoundExprVisitor<TResult>
{
    public TResult Visit(BoundExpr e) => e switch
    {
        BoundLitInt n           => VisitLitInt(n),
        BoundLitFloat f         => VisitLitFloat(f),
        BoundLitStr s           => VisitLitStr(s),
        BoundLitBool b          => VisitLitBool(b),
        BoundLitNull n          => VisitLitNull(n),
        BoundLitChar c          => VisitLitChar(c),
        BoundDefault d          => VisitDefault(d),
        BoundInterpolatedStr i  => VisitInterpolatedStr(i),
        BoundIdent id           => VisitIdent(id),
        BoundCapturedIdent ci   => VisitCapturedIdent(ci),
        BoundAssign a           => VisitAssign(a),
        BoundBinary b           => VisitBinary(b),
        BoundUnary u            => VisitUnary(u),
        BoundPostfix p          => VisitPostfix(p),
        BoundLambda l           => VisitLambda(l),
        BoundCall c             => VisitCall(c),
        BoundIndirectCall ic    => VisitIndirectCall(ic),
        BoundModifiedArg m      => VisitModifiedArg(m),
        BoundMember m           => VisitMember(m),
        BoundIndex i            => VisitIndex(i),
        BoundCast c             => VisitCast(c),
        BoundNew n              => VisitNew(n),
        BoundArrayCreate ac     => VisitArrayCreate(ac),
        BoundArrayLit al        => VisitArrayLit(al),
        BoundConditional c      => VisitConditional(c),
        BoundNullCoalesce nc    => VisitNullCoalesce(nc),
        BoundNullConditional nc => VisitNullConditional(nc),
        BoundIsPattern ip       => VisitIsPattern(ip),
        BoundSwitchExpr s       => VisitSwitchExpr(s),
        BoundError err          => VisitError(err),
        _ => throw new InvalidOperationException(
            $"BoundExprVisitor: unhandled BoundExpr subtype `{e.GetType().Name}` (ICE — add a case to the base switch)")
    };

    protected abstract TResult VisitLitInt(BoundLitInt n);
    protected abstract TResult VisitLitFloat(BoundLitFloat f);
    protected abstract TResult VisitLitStr(BoundLitStr s);
    protected abstract TResult VisitLitBool(BoundLitBool b);
    protected abstract TResult VisitLitNull(BoundLitNull n);
    protected abstract TResult VisitLitChar(BoundLitChar c);
    protected abstract TResult VisitDefault(BoundDefault d);
    protected abstract TResult VisitInterpolatedStr(BoundInterpolatedStr i);
    protected abstract TResult VisitIdent(BoundIdent id);
    protected abstract TResult VisitCapturedIdent(BoundCapturedIdent ci);
    protected abstract TResult VisitAssign(BoundAssign a);
    protected abstract TResult VisitBinary(BoundBinary b);
    protected abstract TResult VisitUnary(BoundUnary u);
    protected abstract TResult VisitPostfix(BoundPostfix p);
    protected abstract TResult VisitLambda(BoundLambda l);
    protected abstract TResult VisitCall(BoundCall c);
    protected abstract TResult VisitIndirectCall(BoundIndirectCall ic);
    protected abstract TResult VisitModifiedArg(BoundModifiedArg m);
    protected abstract TResult VisitMember(BoundMember m);
    protected abstract TResult VisitIndex(BoundIndex i);
    protected abstract TResult VisitCast(BoundCast c);
    protected abstract TResult VisitNew(BoundNew n);
    protected abstract TResult VisitArrayCreate(BoundArrayCreate ac);
    protected abstract TResult VisitArrayLit(BoundArrayLit al);
    protected abstract TResult VisitConditional(BoundConditional c);
    protected abstract TResult VisitNullCoalesce(BoundNullCoalesce nc);
    protected abstract TResult VisitNullConditional(BoundNullConditional nc);
    protected abstract TResult VisitIsPattern(BoundIsPattern ip);
    protected abstract TResult VisitSwitchExpr(BoundSwitchExpr s);
    protected abstract TResult VisitError(BoundError err);
}

/// Default void-walker: leaves are no-ops, interior nodes recurse on children.
/// Subclasses override only what they care about. Mirrors Clang RecursiveASTVisitor.
public abstract class BoundExprWalker : BoundExprVisitor<Unit>
{
    // ── Leaves: no children to recurse into ───────────────────────────────────
    protected override Unit VisitLitInt(BoundLitInt n)        => default;
    protected override Unit VisitLitFloat(BoundLitFloat f)    => default;
    protected override Unit VisitLitStr(BoundLitStr s)        => default;
    protected override Unit VisitLitBool(BoundLitBool b)      => default;
    protected override Unit VisitLitNull(BoundLitNull n)      => default;
    protected override Unit VisitLitChar(BoundLitChar c)      => default;
    protected override Unit VisitDefault(BoundDefault d)      => default;
    protected override Unit VisitIdent(BoundIdent id)         => default;
    protected override Unit VisitCapturedIdent(BoundCapturedIdent ci) => default;
    protected override Unit VisitError(BoundError err)        => default;

    // ── Interior expressions: recurse into BoundExpr children ─────────────────
    protected override Unit VisitInterpolatedStr(BoundInterpolatedStr i)
    {
        foreach (var part in i.Parts)
            if (part is BoundExprPart ep) Visit(ep.Inner);
        return default;
    }

    protected override Unit VisitAssign(BoundAssign a)
    {
        Visit(a.Target);
        Visit(a.Value);
        return default;
    }

    protected override Unit VisitBinary(BoundBinary b)
    {
        Visit(b.Left);
        Visit(b.Right);
        return default;
    }

    protected override Unit VisitUnary(BoundUnary u)
    {
        Visit(u.Operand);
        return default;
    }

    protected override Unit VisitPostfix(BoundPostfix p)
    {
        Visit(p.Operand);
        return default;
    }

    protected override Unit VisitLambda(BoundLambda l)
    {
        switch (l.Body)
        {
            case BoundLambdaExprBody eb: Visit(eb.Expr); break;
            case BoundLambdaBlockBody bb: WalkBlock(bb.Block); break;
        }
        return default;
    }

    protected override Unit VisitCall(BoundCall c)
    {
        if (c.Receiver != null) Visit(c.Receiver);
        foreach (var a in c.Args) Visit(a);
        return default;
    }

    protected override Unit VisitIndirectCall(BoundIndirectCall ic)
    {
        Visit(ic.Callee);
        foreach (var a in ic.Args) Visit(a);
        return default;
    }

    protected override Unit VisitModifiedArg(BoundModifiedArg m)
    {
        Visit(m.Inner);
        return default;
    }

    protected override Unit VisitMember(BoundMember m)
    {
        Visit(m.Target);
        return default;
    }

    protected override Unit VisitIndex(BoundIndex i)
    {
        Visit(i.Target);
        Visit(i.Index);
        return default;
    }

    protected override Unit VisitCast(BoundCast c)
    {
        Visit(c.Operand);
        return default;
    }

    protected override Unit VisitNew(BoundNew n)
    {
        foreach (var a in n.Args) Visit(a);
        return default;
    }

    protected override Unit VisitArrayCreate(BoundArrayCreate ac)
    {
        Visit(ac.Size);
        return default;
    }

    protected override Unit VisitArrayLit(BoundArrayLit al)
    {
        foreach (var e in al.Elements) Visit(e);
        return default;
    }

    protected override Unit VisitConditional(BoundConditional c)
    {
        Visit(c.Cond);
        Visit(c.Then);
        Visit(c.Else);
        return default;
    }

    protected override Unit VisitNullCoalesce(BoundNullCoalesce nc)
    {
        Visit(nc.Left);
        Visit(nc.Right);
        return default;
    }

    protected override Unit VisitNullConditional(BoundNullConditional nc)
    {
        Visit(nc.Target);
        return default;
    }

    protected override Unit VisitIsPattern(BoundIsPattern ip)
    {
        Visit(ip.Target);
        return default;
    }

    protected override Unit VisitSwitchExpr(BoundSwitchExpr s)
    {
        Visit(s.Subject);
        foreach (var arm in s.Arms)
        {
            if (arm.Pattern != null) Visit(arm.Pattern);
            Visit(arm.Body);
        }
        return default;
    }

    /// Helper for subclasses that need to walk a BoundBlock through this
    /// expression walker. Statements are NOT visited here — subclasses that
    /// need statement traversal should compose with BoundStmtWalker.
    protected virtual void WalkBlock(BoundBlock block)
    {
        // Default: no-op. Subclasses that compose with a stmt walker override.
    }
}
