using System.Collections.Generic;
using Z42.Semantics.Bound;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// 2026-05-02 impl-closure-l3-escape-stack v1.
///
/// 给每个函数体的 BoundBlock 跑一遍最简 escape 分析：找出"capturing lambda
/// 赋给 local var，且该 var 仅作为 BoundCall.Receiver（callee 位置）使用、
/// 从不重赋值 / 作 arg / return / 写字段 / 写数组 / 被嵌套 lambda 捕获"
/// 的模式。命中的 BoundLambda 加入返回的 set，Codegen 据此 emit
/// <c>MkClosInstr.StackAlloc=true</c>，VM 走 frame-local arena。
///
/// 设计原则（参见 spec 阶段 2 + design.md Decision 2）：
/// - 保守优先：任何不确定的情形 fallback heap，绝不冒 use-after-free 风险。
/// - 仅做"局部 var → 立即调用"模式；流敏感 / 跨函数 / 复杂别名链全部退化 heap。
/// - 不修改 BoundBlock；产物是 IReadOnlySet&lt;BoundLambda&gt;，由
///   <see cref="SemanticModel.StackAllocClosures"/> 暴露给 Codegen。
///
/// introduce-bound-visitor (2026-05-10): 三处手写 switch（CollectCandidatesStmt
/// / ScanStmt / ScanExpr）迁移到 BoundStmtVisitor / BoundExprVisitor 子类。
/// 行为完全保留 —— legacy fall-through case 显式 override 为 default no-op；
/// `calleePosition` 状态由 EscapeExprScanner 实例字段持有，VisitCall.Receiver
/// 期间临时翻转。
/// </summary>
internal static class ClosureEscapeAnalyzer
{
    public static IReadOnlySet<BoundLambda> Analyze(
        IReadOnlyDictionary<Z42.Syntax.Parser.FunctionDecl, BoundBlock> bodies)
    {
        var stackable = new HashSet<BoundLambda>(ReferenceEqualityComparer.Instance);
        foreach (var body in bodies.Values)
            AnalyzeFunction(body, stackable);
        return stackable;
    }

    private static void AnalyzeFunction(BoundBlock body, HashSet<BoundLambda> stackable)
    {
        // Pass 1: 找出 candidate (var_name → BoundLambda)。仅在 BoundVarDecl
        // 顶层 init 位置接受 lambda；嵌套表达式里冒出的 lambda 不当 candidate。
        var candidates = new Dictionary<string, BoundLambda>();
        var collector = new CandidateCollector(candidates);
        collector.WalkBlock(body);
        if (candidates.Count == 0) return;

        // Pass 2: 扫所有 BoundExpr / BoundStmt，找 BoundIdent 引用 candidate 名字
        // 且**不在 BoundCall.Receiver 位置**的 use。任何这种 use → 该 var 逃逸，
        // 移出 candidates。结束时还在 candidates 的 lambda 全部加入 stackable。
        var escaped = new HashSet<string>();
        var scanner = new EscapeStmtScanner(candidates, escaped);
        scanner.WalkBlock(body);

        foreach (var (name, lambda) in candidates)
        {
            if (escaped.Contains(name)) continue;
            // 仅当 lambda 有 captures 时栈分配才有意义（无 capture 走 LoadFn 路径，
            // 不经 MkClos）。
            if (lambda.Captures.Count == 0) continue;
            stackable.Add(lambda);
        }
    }

    // ── Pass 1: candidate 收集 ───────────────────────────────────────────────

    private sealed class CandidateCollector : BoundStmtVisitor<Unit>
    {
        private readonly Dictionary<string, BoundLambda> _map;
        public CandidateCollector(Dictionary<string, BoundLambda> map) { _map = map; }

        public void WalkBlock(BoundBlock block)
        {
            foreach (var s in block.Stmts) Visit(s);
        }

        protected override Unit VisitVarDecl(BoundVarDecl vd)
        {
            // 同名变量重声明：保守抛弃（不该发生，TypeChecker 已禁止 duplicate）
            if (vd.Init is BoundLambda lam) _map[vd.Name] = lam;
            return default;
        }

        protected override Unit VisitBlockStmt(BoundBlockStmt bs)
        {
            WalkBlock(bs.Block);
            return default;
        }

        protected override Unit VisitIf(BoundIf ifs)
        {
            WalkBlock(ifs.Then);
            if (ifs.Else is BoundBlockStmt elseBlock) WalkBlock(elseBlock.Block);
            else if (ifs.Else is BoundIf nestedIf)    Visit(nestedIf);
            return default;
        }

        protected override Unit VisitWhile(BoundWhile w)         { WalkBlock(w.Body); return default; }
        protected override Unit VisitDoWhile(BoundDoWhile dw)    { WalkBlock(dw.Body); return default; }
        protected override Unit VisitFor(BoundFor f)             { WalkBlock(f.Body); return default; }
        protected override Unit VisitForeach(BoundForeach fe)    { WalkBlock(fe.Body); return default; }

        protected override Unit VisitTryCatch(BoundTryCatch tc)
        {
            WalkBlock(tc.TryBody);
            foreach (var c in tc.Catches) WalkBlock(c.Body);
            if (tc.Finally is { } fin) WalkBlock(fin);
            return default;
        }

        protected override Unit VisitSwitch(BoundSwitch sw)
        {
            foreach (var cse in sw.Cases)
                foreach (var bs2 in cse.Body)
                    Visit(bs2);
            return default;
        }

        protected override Unit VisitLocalFunction(BoundLocalFunction lf)
        {
            WalkBlock(lf.Body);
            return default;
        }

        protected override Unit VisitPinned(BoundPinned pn)
        {
            WalkBlock(pn.Body);
            return default;
        }

        // ── Stmts the legacy switch ignored ───────────────────────────────────
        protected override Unit VisitExprStmt(BoundExprStmt e)   => default;
        protected override Unit VisitReturn(BoundReturn r)       => default;
        protected override Unit VisitThrow(BoundThrow th)        => default;
        protected override Unit VisitBreak(BoundBreak br)        => default;
        protected override Unit VisitContinue(BoundContinue co)  => default;
    }

    // ── Pass 2: 逃逸扫描 ─────────────────────────────────────────────────────

    private sealed class EscapeStmtScanner : BoundStmtVisitor<Unit>
    {
        private readonly Dictionary<string, BoundLambda> _candidates;
        private readonly HashSet<string> _escaped;
        private readonly EscapeExprScanner _expr;

        public EscapeStmtScanner(Dictionary<string, BoundLambda> candidates, HashSet<string> escaped)
        {
            _candidates = candidates;
            _escaped = escaped;
            _expr = new EscapeExprScanner(candidates, escaped);
        }

        public void WalkBlock(BoundBlock block)
        {
            foreach (var s in block.Stmts) Visit(s);
        }

        protected override Unit VisitVarDecl(BoundVarDecl vd)
        {
            // VarDecl 自己 init 的 lambda 不算 use；其他子表达式扫描
            if (vd.Init is { } init && !(init is BoundLambda))
                _expr.Scan(init, calleePosition: false);
            return default;
        }

        protected override Unit VisitExprStmt(BoundExprStmt es)
        {
            _expr.Scan(es.Expr, calleePosition: false);
            return default;
        }

        protected override Unit VisitReturn(BoundReturn r)
        {
            if (r.Value is { } rv) _expr.Scan(rv, calleePosition: false);
            return default;
        }

        protected override Unit VisitIf(BoundIf ifs)
        {
            _expr.Scan(ifs.Cond, calleePosition: false);
            WalkBlock(ifs.Then);
            if (ifs.Else is BoundBlockStmt elseBlock) WalkBlock(elseBlock.Block);
            else if (ifs.Else is BoundIf elseIf) Visit(elseIf);
            return default;
        }

        protected override Unit VisitWhile(BoundWhile w)
        {
            _expr.Scan(w.Cond, calleePosition: false);
            WalkBlock(w.Body);
            return default;
        }

        protected override Unit VisitDoWhile(BoundDoWhile dw)
        {
            WalkBlock(dw.Body);
            _expr.Scan(dw.Cond, calleePosition: false);
            return default;
        }

        protected override Unit VisitFor(BoundFor f)
        {
            if (f.Init is { } fi) Visit(fi);
            if (f.Cond is { } fc) _expr.Scan(fc, calleePosition: false);
            if (f.Increment is { } fs) _expr.Scan(fs, calleePosition: false);
            WalkBlock(f.Body);
            return default;
        }

        protected override Unit VisitForeach(BoundForeach fe)
        {
            _expr.Scan(fe.Collection, calleePosition: false);
            WalkBlock(fe.Body);
            return default;
        }

        protected override Unit VisitBlockStmt(BoundBlockStmt bs)
        {
            WalkBlock(bs.Block);
            return default;
        }

        protected override Unit VisitThrow(BoundThrow t)
        {
            _expr.Scan(t.Value, calleePosition: false);
            return default;
        }

        protected override Unit VisitTryCatch(BoundTryCatch tc)
        {
            WalkBlock(tc.TryBody);
            foreach (var c in tc.Catches) WalkBlock(c.Body);
            if (tc.Finally is { } fin) WalkBlock(fin);
            return default;
        }

        protected override Unit VisitSwitch(BoundSwitch sw)
        {
            _expr.Scan(sw.Subject, calleePosition: false);
            foreach (var cse in sw.Cases)
            {
                if (cse.Pattern is { } pat) _expr.Scan(pat, calleePosition: false);
                foreach (var bs2 in cse.Body) Visit(bs2);
            }
            return default;
        }

        protected override Unit VisitLocalFunction(BoundLocalFunction lf)
        {
            // Local function body 内引用 candidate 走 BoundCapturedIdent → escape
            WalkBlock(lf.Body);
            return default;
        }

        protected override Unit VisitPinned(BoundPinned pn)
        {
            _expr.Scan(pn.Source, calleePosition: false);
            WalkBlock(pn.Body);
            return default;
        }

        // ── Stmts with no expression (legacy switch ignored) ──────────────────
        protected override Unit VisitBreak(BoundBreak br)        => default;
        protected override Unit VisitContinue(BoundContinue co)  => default;
    }

    private sealed class EscapeExprScanner : BoundExprVisitor<Unit>
    {
        private readonly Dictionary<string, BoundLambda> _candidates;
        private readonly HashSet<string> _escaped;
        private bool _calleePosition;

        public EscapeExprScanner(Dictionary<string, BoundLambda> candidates, HashSet<string> escaped)
        {
            _candidates = candidates;
            _escaped = escaped;
        }

        /// Public entry that sets the callee-position state for a scan root.
        public void Scan(BoundExpr e, bool calleePosition)
        {
            var prev = _calleePosition;
            _calleePosition = calleePosition;
            Visit(e);
            _calleePosition = prev;
        }

        protected override Unit VisitIdent(BoundIdent id)
        {
            // 只有当 id.Name 是 candidate 且**非 callee 位置**才标 escape
            if (_candidates.ContainsKey(id.Name) && !_calleePosition)
                _escaped.Add(id.Name);
            return default;
        }

        protected override Unit VisitCapturedIdent(BoundCapturedIdent ci)
        {
            // 被嵌套 lambda 捕获 → 一律 escape（env 跨边界外溢）
            if (_candidates.ContainsKey(ci.Name)) _escaped.Add(ci.Name);
            return default;
        }

        protected override Unit VisitCall(BoundCall call)
        {
            // Receiver 是唯一 callee 位置：用 BoundIdent 时 calleePosition=true
            if (call.Receiver is { } recv) Scan(recv, calleePosition: true);
            foreach (var a in call.Args) Scan(a, calleePosition: false);
            return default;
        }

        protected override Unit VisitIndirectCall(BoundIndirectCall ic)
        {
            // Indirect call: callee is a function VALUE expression, not a method
            // reference. Therefore it counts as **callee position** for the
            // escape analysis — the candidate is being invoked, not stored or
            // passed elsewhere. This preserves the previous behavior where
            // `var f = () => x; f();` did not mark `f` as escaped.
            Scan(ic.Callee, calleePosition: true);
            foreach (var a in ic.Args) Scan(a, calleePosition: false);
            return default;
        }

        protected override Unit VisitAssign(BoundAssign asn)
        {
            // 赋值 target / value 都不是 callee 位置
            Scan(asn.Target, calleePosition: false);
            Scan(asn.Value,  calleePosition: false);
            // target 是 BoundIdent 且名字是 candidate → 重赋值，escape
            if (asn.Target is BoundIdent tid && _candidates.ContainsKey(tid.Name))
                _escaped.Add(tid.Name);
            return default;
        }

        protected override Unit VisitBinary(BoundBinary bin)
        {
            Scan(bin.Left,  calleePosition: false);
            Scan(bin.Right, calleePosition: false);
            return default;
        }

        protected override Unit VisitUnary(BoundUnary u)
        {
            Scan(u.Operand, calleePosition: false);
            return default;
        }

        protected override Unit VisitPostfix(BoundPostfix p)
        {
            Scan(p.Operand, calleePosition: false);
            return default;
        }

        protected override Unit VisitConditional(BoundConditional cd)
        {
            Scan(cd.Cond, calleePosition: false);
            Scan(cd.Then, calleePosition: false);
            Scan(cd.Else, calleePosition: false);
            return default;
        }

        protected override Unit VisitNullCoalesce(BoundNullCoalesce nc)
        {
            Scan(nc.Left,  calleePosition: false);
            Scan(nc.Right, calleePosition: false);
            return default;
        }

        protected override Unit VisitNullConditional(BoundNullConditional ncd)
        {
            Scan(ncd.Target, calleePosition: false);
            return default;
        }

        protected override Unit VisitIsPattern(BoundIsPattern ip)
        {
            Scan(ip.Target, calleePosition: false);
            return default;
        }

        protected override Unit VisitCast(BoundCast cs)
        {
            Scan(cs.Operand, calleePosition: false);
            return default;
        }

        protected override Unit VisitMember(BoundMember mb)
        {
            Scan(mb.Target, calleePosition: false);
            return default;
        }

        protected override Unit VisitIndex(BoundIndex ix)
        {
            Scan(ix.Target, calleePosition: false);
            Scan(ix.Index,  calleePosition: false);
            return default;
        }

        protected override Unit VisitNew(BoundNew nw)
        {
            foreach (var a in nw.Args) Scan(a, calleePosition: false);
            return default;
        }

        protected override Unit VisitArrayCreate(BoundArrayCreate ac)
        {
            Scan(ac.Size, calleePosition: false);
            return default;
        }

        protected override Unit VisitArrayLit(BoundArrayLit al)
        {
            foreach (var e in al.Elements) Scan(e, calleePosition: false);
            return default;
        }

        protected override Unit VisitInterpolatedStr(BoundInterpolatedStr istr)
        {
            foreach (var part in istr.Parts)
                if (part is BoundExprPart ep) Scan(ep.Inner, calleePosition: false);
            return default;
        }

        protected override Unit VisitLambda(BoundLambda inner)
        {
            // 嵌套 lambda 内部的 capture 列表里出现 candidate 就视为 escape；
            // BindIdent 已把 outer 引用转成 BoundCapturedIdent，所以 inner.Body
            // 走 ScanForEscape* 会通过 BoundCapturedIdent case 处理。
            // 这里也扫 inner body 以防有非 capture 的子表达式包含 candidate。
            ScanLambdaBody(inner.Body);
            return default;
        }

        // ── Legacy switch's default fall-through (no candidate effect) ────────
        protected override Unit VisitLitInt(BoundLitInt n)              => default;
        protected override Unit VisitLitFloat(BoundLitFloat f)          => default;
        protected override Unit VisitLitStr(BoundLitStr s)              => default;
        protected override Unit VisitLitBool(BoundLitBool b)            => default;
        protected override Unit VisitLitNull(BoundLitNull n)            => default;
        protected override Unit VisitLitChar(BoundLitChar c)            => default;
        protected override Unit VisitDefault(BoundDefault d)            => default;
        protected override Unit VisitModifiedArg(BoundModifiedArg m)    => default;
        protected override Unit VisitSwitchExpr(BoundSwitchExpr s)      => default;
        protected override Unit VisitError(BoundError err)              => default;

        private void ScanLambdaBody(BoundLambdaBody body)
        {
            switch (body)
            {
                case BoundLambdaExprBody eb:
                    Scan(eb.Expr, calleePosition: false);
                    break;
                case BoundLambdaBlockBody bb:
                    // Reuse the outer Pass 2 stmt scanner shape — both share
                    // the same _candidates / _escaped state. A fresh instance
                    // is fine because Scan always saves/restores _calleePosition.
                    new EscapeStmtScanner(_candidates, _escaped).WalkBlock(bb.Block);
                    break;
            }
        }
    }
}
