using System.Reflection;
using FluentAssertions;
using Xunit;
using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

/// introduce-bound-visitor (2026-05-10) — verifies the BoundExprVisitor /
/// BoundStmtVisitor base classes dispatch every concrete leaf in the
/// hierarchy and that the default Walker recurses into children.
///
/// 这些测试是 visitor 框架的"宪法":新增一个 BoundXxx 节点而忘改 base switch
/// 时,reflection-driven dispatch 测试会立即红。
public sealed class BoundVisitorTests
{
    private static readonly Span S = new(0, 0, 1, 1);

    // ── Reflection-driven dispatch coverage ───────────────────────────────────

    [Fact]
    public void Visit_AllConcreteBoundExprTypes_DispatchesCorrectly()
    {
        var leafTypes = ConcreteSubclassesOf(typeof(BoundExpr));
        leafTypes.Should().NotBeEmpty("BoundExpr must have concrete leaves");

        var visitor = new SeenVisitor();
        foreach (var t in leafTypes)
        {
            var node = ConstructBoundExpr(t);
            visitor.Visit(node);
        }

        visitor.Seen.Should().BeEquivalentTo(
            leafTypes.Select(t => t.Name),
            "every concrete BoundExpr leaf must dispatch through the base switch");
    }

    [Fact]
    public void Visit_AllConcreteBoundStmtTypes_DispatchesCorrectly()
    {
        var leafTypes = ConcreteSubclassesOf(typeof(BoundStmt));
        leafTypes.Should().NotBeEmpty("BoundStmt must have concrete leaves");

        var visitor = new SeenStmtVisitor();
        foreach (var t in leafTypes)
        {
            var node = ConstructBoundStmt(t);
            visitor.Visit(node);
        }

        visitor.Seen.Should().BeEquivalentTo(
            leafTypes.Select(t => t.Name),
            "every concrete BoundStmt leaf must dispatch through the base switch");
    }

    // ── Walker default-recursion behavior ─────────────────────────────────────

    [Fact]
    public void Walker_BoundBinaryWithTwoLiterals_VisitsThreeNodes()
    {
        var bin = new BoundBinary(
            BinaryOp.Add,
            new BoundLitInt(1, Z42Type.Int, S),
            new BoundLitInt(2, Z42Type.Int, S),
            Z42Type.Int, S);

        var counter = new CountingExprWalker();
        counter.Visit(bin);

        counter.Count.Should().Be(3, "1 binary + 2 literal children");
    }

    [Fact]
    public void Walker_BoundCallWithReceiverAndArgs_VisitsAllChildren()
    {
        var call = new BoundCall(
            BoundCallKind.Instance,
            Receiver: new BoundLitInt(0, Z42Type.Int, S),
            ReceiverClass: "Foo",
            MethodName: "bar",
            CalleeName: null,
            Args: new BoundExpr[]
            {
                new BoundLitInt(1, Z42Type.Int, S),
                new BoundLitStr("x", S),
            },
            RetType: Z42Type.Int,
            Span: S);

        var counter = new CountingExprWalker();
        counter.Visit(call);

        counter.Count.Should().Be(4, "1 call + 1 receiver + 2 args");
    }

    [Fact]
    public void StmtWalker_BoundIfWithBlocks_VisitsNestedStmts()
    {
        var thenBlock = new BoundBlock(
            new BoundStmt[]
            {
                new BoundExprStmt(new BoundLitInt(1, Z42Type.Int, S), S),
                new BoundExprStmt(new BoundLitInt(2, Z42Type.Int, S), S),
            }, S);
        var elseBranch = new BoundBlockStmt(
            new BoundBlock(new BoundStmt[]
            {
                new BoundExprStmt(new BoundLitInt(3, Z42Type.Int, S), S),
            }, S), S);
        var ifStmt = new BoundIf(
            new BoundLitBool(true, S),
            thenBlock,
            elseBranch,
            S);

        var counter = new CountingStmtWalker();
        counter.Visit(ifStmt);

        // BoundIf itself + 2 ExprStmts in then + BoundBlockStmt + 1 ExprStmt inside.
        counter.Count.Should().Be(5);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<Type> ConcreteSubclassesOf(Type baseType)
    {
        return baseType.Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(baseType) && !t.IsAbstract)
            .OrderBy(t => t.Name)
            .ToList();
    }

    /// Construct an instance of a BoundExpr leaf using reflection. We pick the
    /// first public constructor and supply default values per parameter type —
    /// the tests only verify dispatch reaches VisitXxx, not field correctness.
    private static BoundExpr ConstructBoundExpr(Type t)
        => (BoundExpr)Construct(t);

    private static BoundStmt ConstructBoundStmt(Type t)
        => (BoundStmt)Construct(t);

    private static object Construct(Type t)
    {
        var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p => DefaultArg(p.ParameterType)).ToArray();
        return ctor.Invoke(args)!;
    }

    private static object? DefaultArg(Type t)
    {
        if (t == typeof(Span))                                  return S;
        if (t == typeof(string))                                return "x";
        if (t == typeof(bool))                                  return false;
        if (t == typeof(char))                                  return ' ';
        if (t == typeof(long))                                  return 0L;
        if (t == typeof(int))                                   return 0;
        if (t == typeof(short))                                 return (short)0;
        if (t == typeof(byte))                                  return (byte)0;
        if (t == typeof(double))                                return 0.0;
        if (t == typeof(float))                                 return 0.0f;
        if (t == typeof(Z42Type))                               return Z42Type.Int;
        if (t == typeof(Z42FuncType))                           return new Z42FuncType(
                                                                    Array.Empty<Z42Type>(), Z42Type.Int);
        if (t == typeof(BoundExpr))                             return new BoundLitInt(0, Z42Type.Int, S);
        if (t == typeof(BoundLambdaBody))                       return new BoundLambdaExprBody(
                                                                    new BoundLitInt(0, Z42Type.Int, S), S);
        if (t == typeof(BoundBlock))                            return new BoundBlock(
                                                                    Array.Empty<BoundStmt>(), S);
        if (t == typeof(BoundStmt))                             return new BoundExprStmt(
                                                                    new BoundLitInt(0, Z42Type.Int, S), S);
        if (t == typeof(BoundOutVarDecl))                       return null;  // nullable
        if (t.IsEnum)                                           return Activator.CreateInstance(t);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var elem = t.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elem);
            return Activator.CreateInstance(listType)!;
        }
        if (Nullable.GetUnderlyingType(t) != null)              return null;
        if (!t.IsValueType)                                     return null;  // reference types: null OK
        return Activator.CreateInstance(t);
    }

    // ── Test visitor types ────────────────────────────────────────────────────

    private sealed class SeenVisitor : BoundExprVisitor<Unit>
    {
        public HashSet<string> Seen { get; } = new();
        private Unit Mark(BoundExpr e) { Seen.Add(e.GetType().Name); return default; }

        protected override Unit VisitLitInt(BoundLitInt n)              => Mark(n);
        protected override Unit VisitLitFloat(BoundLitFloat f)          => Mark(f);
        protected override Unit VisitLitStr(BoundLitStr s)              => Mark(s);
        protected override Unit VisitLitBool(BoundLitBool b)            => Mark(b);
        protected override Unit VisitLitNull(BoundLitNull n)            => Mark(n);
        protected override Unit VisitLitChar(BoundLitChar c)            => Mark(c);
        protected override Unit VisitDefault(BoundDefault d)            => Mark(d);
        protected override Unit VisitInterpolatedStr(BoundInterpolatedStr i) => Mark(i);
        protected override Unit VisitIdent(BoundIdent id)               => Mark(id);
        protected override Unit VisitCapturedIdent(BoundCapturedIdent ci) => Mark(ci);
        protected override Unit VisitAssign(BoundAssign a)              => Mark(a);
        protected override Unit VisitBinary(BoundBinary b)              => Mark(b);
        protected override Unit VisitUnary(BoundUnary u)                => Mark(u);
        protected override Unit VisitPostfix(BoundPostfix p)            => Mark(p);
        protected override Unit VisitLambda(BoundLambda l)              => Mark(l);
        protected override Unit VisitCall(BoundCall c)                  => Mark(c);
        protected override Unit VisitModifiedArg(BoundModifiedArg m)    => Mark(m);
        protected override Unit VisitMember(BoundMember m)              => Mark(m);
        protected override Unit VisitIndex(BoundIndex i)                => Mark(i);
        protected override Unit VisitCast(BoundCast c)                  => Mark(c);
        protected override Unit VisitNew(BoundNew n)                    => Mark(n);
        protected override Unit VisitArrayCreate(BoundArrayCreate ac)   => Mark(ac);
        protected override Unit VisitArrayLit(BoundArrayLit al)         => Mark(al);
        protected override Unit VisitConditional(BoundConditional c)    => Mark(c);
        protected override Unit VisitNullCoalesce(BoundNullCoalesce nc) => Mark(nc);
        protected override Unit VisitNullConditional(BoundNullConditional nc) => Mark(nc);
        protected override Unit VisitIsPattern(BoundIsPattern ip)       => Mark(ip);
        protected override Unit VisitSwitchExpr(BoundSwitchExpr s)      => Mark(s);
        protected override Unit VisitError(BoundError err)              => Mark(err);
    }

    private sealed class SeenStmtVisitor : BoundStmtVisitor<Unit>
    {
        public HashSet<string> Seen { get; } = new();
        private Unit Mark(BoundStmt s) { Seen.Add(s.GetType().Name); return default; }

        protected override Unit VisitBlockStmt(BoundBlockStmt b)         => Mark(b);
        protected override Unit VisitVarDecl(BoundVarDecl v)             => Mark(v);
        protected override Unit VisitReturn(BoundReturn r)               => Mark(r);
        protected override Unit VisitExprStmt(BoundExprStmt e)           => Mark(e);
        protected override Unit VisitIf(BoundIf i)                       => Mark(i);
        protected override Unit VisitWhile(BoundWhile w)                 => Mark(w);
        protected override Unit VisitDoWhile(BoundDoWhile dw)            => Mark(dw);
        protected override Unit VisitFor(BoundFor f)                     => Mark(f);
        protected override Unit VisitForeach(BoundForeach fe)            => Mark(fe);
        protected override Unit VisitBreak(BoundBreak br)                => Mark(br);
        protected override Unit VisitContinue(BoundContinue co)          => Mark(co);
        protected override Unit VisitSwitch(BoundSwitch sw)              => Mark(sw);
        protected override Unit VisitTryCatch(BoundTryCatch tc)          => Mark(tc);
        protected override Unit VisitThrow(BoundThrow th)                => Mark(th);
        protected override Unit VisitLocalFunction(BoundLocalFunction lf) => Mark(lf);
        protected override Unit VisitPinned(BoundPinned p)               => Mark(p);
    }

    private sealed class CountingExprWalker : BoundExprWalker
    {
        public int Count { get; private set; }

        // Override every leaf+interior to count the visit; rely on base
        // implementation to also recurse into children for interior nodes.
        protected override Unit VisitLitInt(BoundLitInt n)            { Count++; return base.VisitLitInt(n); }
        protected override Unit VisitLitFloat(BoundLitFloat f)        { Count++; return base.VisitLitFloat(f); }
        protected override Unit VisitLitStr(BoundLitStr s)            { Count++; return base.VisitLitStr(s); }
        protected override Unit VisitLitBool(BoundLitBool b)          { Count++; return base.VisitLitBool(b); }
        protected override Unit VisitLitNull(BoundLitNull n)          { Count++; return base.VisitLitNull(n); }
        protected override Unit VisitLitChar(BoundLitChar c)          { Count++; return base.VisitLitChar(c); }
        protected override Unit VisitDefault(BoundDefault d)          { Count++; return base.VisitDefault(d); }
        protected override Unit VisitInterpolatedStr(BoundInterpolatedStr i) { Count++; return base.VisitInterpolatedStr(i); }
        protected override Unit VisitIdent(BoundIdent id)             { Count++; return base.VisitIdent(id); }
        protected override Unit VisitCapturedIdent(BoundCapturedIdent ci) { Count++; return base.VisitCapturedIdent(ci); }
        protected override Unit VisitAssign(BoundAssign a)            { Count++; return base.VisitAssign(a); }
        protected override Unit VisitBinary(BoundBinary b)            { Count++; return base.VisitBinary(b); }
        protected override Unit VisitUnary(BoundUnary u)              { Count++; return base.VisitUnary(u); }
        protected override Unit VisitPostfix(BoundPostfix p)          { Count++; return base.VisitPostfix(p); }
        protected override Unit VisitLambda(BoundLambda l)            { Count++; return base.VisitLambda(l); }
        protected override Unit VisitCall(BoundCall c)                { Count++; return base.VisitCall(c); }
        protected override Unit VisitModifiedArg(BoundModifiedArg m)  { Count++; return base.VisitModifiedArg(m); }
        protected override Unit VisitMember(BoundMember m)            { Count++; return base.VisitMember(m); }
        protected override Unit VisitIndex(BoundIndex i)              { Count++; return base.VisitIndex(i); }
        protected override Unit VisitCast(BoundCast c)                { Count++; return base.VisitCast(c); }
        protected override Unit VisitNew(BoundNew n)                  { Count++; return base.VisitNew(n); }
        protected override Unit VisitArrayCreate(BoundArrayCreate ac) { Count++; return base.VisitArrayCreate(ac); }
        protected override Unit VisitArrayLit(BoundArrayLit al)       { Count++; return base.VisitArrayLit(al); }
        protected override Unit VisitConditional(BoundConditional c)  { Count++; return base.VisitConditional(c); }
        protected override Unit VisitNullCoalesce(BoundNullCoalesce nc) { Count++; return base.VisitNullCoalesce(nc); }
        protected override Unit VisitNullConditional(BoundNullConditional nc) { Count++; return base.VisitNullConditional(nc); }
        protected override Unit VisitIsPattern(BoundIsPattern ip)     { Count++; return base.VisitIsPattern(ip); }
        protected override Unit VisitSwitchExpr(BoundSwitchExpr s)    { Count++; return base.VisitSwitchExpr(s); }
        protected override Unit VisitError(BoundError err)            { Count++; return base.VisitError(err); }
    }

    private sealed class CountingStmtWalker : BoundStmtWalker
    {
        public int Count { get; private set; }

        protected override Unit VisitBlockStmt(BoundBlockStmt b)         { Count++; return base.VisitBlockStmt(b); }
        protected override Unit VisitVarDecl(BoundVarDecl v)             { Count++; return base.VisitVarDecl(v); }
        protected override Unit VisitReturn(BoundReturn r)               { Count++; return base.VisitReturn(r); }
        protected override Unit VisitExprStmt(BoundExprStmt e)           { Count++; return base.VisitExprStmt(e); }
        protected override Unit VisitIf(BoundIf i)                       { Count++; return base.VisitIf(i); }
        protected override Unit VisitWhile(BoundWhile w)                 { Count++; return base.VisitWhile(w); }
        protected override Unit VisitDoWhile(BoundDoWhile dw)            { Count++; return base.VisitDoWhile(dw); }
        protected override Unit VisitFor(BoundFor f)                     { Count++; return base.VisitFor(f); }
        protected override Unit VisitForeach(BoundForeach fe)            { Count++; return base.VisitForeach(fe); }
        protected override Unit VisitBreak(BoundBreak br)                { Count++; return base.VisitBreak(br); }
        protected override Unit VisitContinue(BoundContinue co)          { Count++; return base.VisitContinue(co); }
        protected override Unit VisitSwitch(BoundSwitch sw)              { Count++; return base.VisitSwitch(sw); }
        protected override Unit VisitTryCatch(BoundTryCatch tc)          { Count++; return base.VisitTryCatch(tc); }
        protected override Unit VisitThrow(BoundThrow th)                { Count++; return base.VisitThrow(th); }
        protected override Unit VisitLocalFunction(BoundLocalFunction lf) { Count++; return base.VisitLocalFunction(lf); }
        protected override Unit VisitPinned(BoundPinned p)               { Count++; return base.VisitPinned(p); }
    }
}
