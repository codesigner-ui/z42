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
        CollectCandidates(body, candidates);
        if (candidates.Count == 0) return;

        // Pass 2: 扫所有 BoundExpr / BoundStmt，找 BoundIdent 引用 candidate 名字
        // 且**不在 BoundCall.Receiver 位置**的 use。任何这种 use → 该 var 逃逸，
        // 移出 candidates。结束时还在 candidates 的 lambda 全部加入 stackable。
        var escaped = new HashSet<string>();
        ScanForEscape(body, candidates, escaped, calleePosition: false);

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

    private static void CollectCandidates(BoundBlock block, Dictionary<string, BoundLambda> map)
    {
        foreach (var stmt in block.Stmts)
            CollectCandidatesStmt(stmt, map);
    }

    private static void CollectCandidatesStmt(BoundStmt stmt, Dictionary<string, BoundLambda> map)
    {
        switch (stmt)
        {
            case BoundVarDecl vd when vd.Init is BoundLambda lam:
                // 同名变量重声明：保守抛弃（不该发生，TypeChecker 已禁止 duplicate）
                map[vd.Name] = lam;
                break;
            case BoundBlockStmt bs:
                CollectCandidates(bs.Block, map);
                break;
            case BoundIf ifs:
                CollectCandidates(ifs.Then, map);
                if (ifs.Else is BoundBlockStmt elseBlock) CollectCandidates(elseBlock.Block, map);
                else if (ifs.Else is BoundIf nestedIf)    CollectCandidatesStmt(nestedIf, map);
                break;
            case BoundWhile w:
                CollectCandidates(w.Body, map);
                break;
            case BoundDoWhile dw:
                CollectCandidates(dw.Body, map);
                break;
            case BoundFor f:
                CollectCandidates(f.Body, map);
                break;
            case BoundForeach fe:
                CollectCandidates(fe.Body, map);
                break;
            case BoundTryCatch tc:
                CollectCandidates(tc.TryBody, map);
                foreach (var c in tc.Catches) CollectCandidates(c.Body, map);
                if (tc.Finally is { } fin) CollectCandidates(fin, map);
                break;
            case BoundSwitch sw:
                foreach (var cse in sw.Cases)
                    foreach (var bs2 in cse.Body)
                        CollectCandidatesStmt(bs2, map);
                break;
            case BoundLocalFunction lf:
                CollectCandidates(lf.Body, map);
                break;
            case BoundPinned pn:
                CollectCandidates(pn.Body, map);
                break;
        }
    }

    // ── Pass 2: 逃逸扫描 ─────────────────────────────────────────────────────

    private static void ScanForEscape(
        BoundBlock block, Dictionary<string, BoundLambda> candidates,
        HashSet<string> escaped, bool calleePosition)
    {
        foreach (var stmt in block.Stmts)
            ScanStmt(stmt, candidates, escaped);
    }

    private static void ScanStmt(BoundStmt stmt, Dictionary<string, BoundLambda> candidates, HashSet<string> escaped)
    {
        switch (stmt)
        {
            case BoundVarDecl vd:
                // VarDecl 自己 init 的 lambda 不算 use；其他子表达式扫描
                if (vd.Init is { } init && !(init is BoundLambda))
                    ScanExpr(init, candidates, escaped, calleePosition: false);
                break;
            case BoundExprStmt es:
                ScanExpr(es.Expr, candidates, escaped, calleePosition: false);
                break;
            case BoundReturn r:
                if (r.Value is { } rv) ScanExpr(rv, candidates, escaped, calleePosition: false);
                break;
            case BoundIf ifs:
                ScanExpr(ifs.Cond, candidates, escaped, calleePosition: false);
                ScanForEscape(ifs.Then, candidates, escaped, calleePosition: false);
                if (ifs.Else is BoundBlockStmt elseBlock)
                    ScanForEscape(elseBlock.Block, candidates, escaped, calleePosition: false);
                else if (ifs.Else is BoundIf elseIf)
                    ScanStmt(elseIf, candidates, escaped);
                break;
            case BoundWhile w:
                ScanExpr(w.Cond, candidates, escaped, calleePosition: false);
                ScanForEscape(w.Body, candidates, escaped, calleePosition: false);
                break;
            case BoundDoWhile dw:
                ScanForEscape(dw.Body, candidates, escaped, calleePosition: false);
                ScanExpr(dw.Cond, candidates, escaped, calleePosition: false);
                break;
            case BoundFor f:
                if (f.Init is { } fi) ScanStmt(fi, candidates, escaped);
                if (f.Cond is { } fc) ScanExpr(fc, candidates, escaped, calleePosition: false);
                if (f.Increment is { } fs) ScanExpr(fs, candidates, escaped, calleePosition: false);
                ScanForEscape(f.Body, candidates, escaped, calleePosition: false);
                break;
            case BoundForeach fe:
                ScanExpr(fe.Collection, candidates, escaped, calleePosition: false);
                ScanForEscape(fe.Body, candidates, escaped, calleePosition: false);
                break;
            case BoundBlockStmt bs:
                ScanForEscape(bs.Block, candidates, escaped, calleePosition: false);
                break;
            case BoundThrow t:
                ScanExpr(t.Value, candidates, escaped, calleePosition: false);
                break;
            case BoundTryCatch tc:
                ScanForEscape(tc.TryBody, candidates, escaped, calleePosition: false);
                foreach (var c in tc.Catches)
                    ScanForEscape(c.Body, candidates, escaped, calleePosition: false);
                if (tc.Finally is { } fin)
                    ScanForEscape(fin, candidates, escaped, calleePosition: false);
                break;
            case BoundSwitch sw:
                ScanExpr(sw.Subject, candidates, escaped, calleePosition: false);
                foreach (var cse in sw.Cases)
                {
                    if (cse.Pattern is { } pat)
                        ScanExpr(pat, candidates, escaped, calleePosition: false);
                    foreach (var bs2 in cse.Body) ScanStmt(bs2, candidates, escaped);
                }
                break;
            case BoundLocalFunction lf:
                // Local function body 内引用 candidate 走 BoundCapturedIdent → escape
                ScanForEscape(lf.Body, candidates, escaped, calleePosition: false);
                break;
            case BoundPinned pn:
                ScanExpr(pn.Source, candidates, escaped, calleePosition: false);
                ScanForEscape(pn.Body, candidates, escaped, calleePosition: false);
                break;
            // BoundBreak / BoundContinue 无表达式
        }
    }

    private static void ScanExpr(
        BoundExpr expr, Dictionary<string, BoundLambda> candidates,
        HashSet<string> escaped, bool calleePosition)
    {
        switch (expr)
        {
            case BoundIdent id:
                // 只有当 id.Name 是 candidate 且**非 callee 位置**才标 escape
                if (candidates.ContainsKey(id.Name) && !calleePosition)
                    escaped.Add(id.Name);
                break;
            case BoundCapturedIdent ci:
                // 被嵌套 lambda 捕获 → 一律 escape（env 跨边界外溢）
                if (candidates.ContainsKey(ci.Name)) escaped.Add(ci.Name);
                break;
            case BoundCall call:
                // Receiver 是唯一 callee 位置：用 BoundIdent 时 calleePosition=true
                if (call.Receiver is { } recv)
                    ScanExpr(recv, candidates, escaped, calleePosition: true);
                foreach (var a in call.Args)
                    ScanExpr(a, candidates, escaped, calleePosition: false);
                break;
            case BoundAssign asn:
                // 赋值 target / value 都不是 callee 位置
                ScanExpr(asn.Target, candidates, escaped, calleePosition: false);
                ScanExpr(asn.Value,  candidates, escaped, calleePosition: false);
                // target 是 BoundIdent 且名字是 candidate → 重赋值，escape
                if (asn.Target is BoundIdent tid && candidates.ContainsKey(tid.Name))
                    escaped.Add(tid.Name);
                break;
            case BoundBinary bin:
                ScanExpr(bin.Left,  candidates, escaped, calleePosition: false);
                ScanExpr(bin.Right, candidates, escaped, calleePosition: false);
                break;
            case BoundUnary u:    ScanExpr(u.Operand, candidates, escaped, calleePosition: false); break;
            case BoundPostfix p:  ScanExpr(p.Operand, candidates, escaped, calleePosition: false); break;
            case BoundConditional cd:
                ScanExpr(cd.Cond, candidates, escaped, calleePosition: false);
                ScanExpr(cd.Then, candidates, escaped, calleePosition: false);
                ScanExpr(cd.Else, candidates, escaped, calleePosition: false);
                break;
            case BoundNullCoalesce nc:
                ScanExpr(nc.Left,  candidates, escaped, calleePosition: false);
                ScanExpr(nc.Right, candidates, escaped, calleePosition: false);
                break;
            case BoundNullConditional ncd:
                ScanExpr(ncd.Target, candidates, escaped, calleePosition: false); break;
            case BoundIsPattern ip:
                ScanExpr(ip.Target, candidates, escaped, calleePosition: false); break;
            case BoundCast cs:
                ScanExpr(cs.Operand, candidates, escaped, calleePosition: false); break;
            case BoundMember mb:
                ScanExpr(mb.Target, candidates, escaped, calleePosition: false); break;
            case BoundIndex ix:
                ScanExpr(ix.Target, candidates, escaped, calleePosition: false);
                ScanExpr(ix.Index,  candidates, escaped, calleePosition: false);
                break;
            case BoundNew nw:
                foreach (var a in nw.Args)
                    ScanExpr(a, candidates, escaped, calleePosition: false);
                break;
            case BoundArrayCreate ac:
                ScanExpr(ac.Size, candidates, escaped, calleePosition: false); break;
            case BoundArrayLit al:
                foreach (var e in al.Elements)
                    ScanExpr(e, candidates, escaped, calleePosition: false);
                break;
            case BoundInterpolatedStr istr:
                foreach (var part in istr.Parts)
                    if (part is BoundExprPart ep)
                        ScanExpr(ep.Inner, candidates, escaped, calleePosition: false);
                break;
            case BoundLambda inner:
                // 嵌套 lambda 内部的 capture 列表里出现 candidate 就视为 escape；
                // BindIdent 已把 outer 引用转成 BoundCapturedIdent，所以 inner.Body
                // 走 ScanForEscape* 会通过 BoundCapturedIdent case 处理。
                // 这里也扫 inner body 以防有非 capture 的子表达式包含 candidate。
                ScanLambdaBody(inner.Body, candidates, escaped);
                break;
            default:
                // BoundLitInt / BoundLitStr / ... / BoundError / BoundFuncRef 等
                // 不影响 candidate
                break;
        }
    }

    private static void ScanLambdaBody(
        BoundLambdaBody body, Dictionary<string, BoundLambda> candidates, HashSet<string> escaped)
    {
        switch (body)
        {
            case BoundLambdaExprBody eb:
                ScanExpr(eb.Expr, candidates, escaped, calleePosition: false);
                break;
            case BoundLambdaBlockBody bb:
                ScanForEscape(bb.Block, candidates, escaped, calleePosition: false);
                break;
        }
    }
}
