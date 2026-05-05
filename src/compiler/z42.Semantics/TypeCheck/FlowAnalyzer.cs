using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Post-binding flow analysis passes — reachability and definite assignment.
/// Operates on already-bound BoundBlocks; no type-checking state needed.
/// Implements <see cref="IFlowAnalyzer"/> for testability; static methods retained for convenience.
/// </summary>
internal sealed class FlowAnalyzer : IFlowAnalyzer
{
    /// Singleton instance for interface-based usage.
    internal static readonly FlowAnalyzer Instance = new();

    // ── IFlowAnalyzer implementation ─────────────────────────────────────────

    bool IFlowAnalyzer.AlwaysReturns(BoundBlock block) => AlwaysReturns(block);
    void IFlowAnalyzer.CheckDefiniteAssignment(
        BoundBlock block, DiagnosticBag diags,
        IReadOnlyList<Param>? functionParams) =>
        CheckDefiniteAssignment(block, diags, functionParams);

    // ── Reachability analysis ────────────────────────────────────────────────

    /// Returns true if ALL control-flow paths through the block reach a return/throw.
    internal static bool AlwaysReturns(BoundBlock block)
    {
        foreach (var stmt in block.Stmts)
            if (StmtAlwaysReturns(stmt)) return true;
        return false;
    }

    private static bool StmtAlwaysReturns(BoundStmt stmt) => stmt switch
    {
        BoundReturn => true,
        BoundThrow => true,
        BoundBlockStmt b => AlwaysReturns(b.Block),
        BoundIf i => i.Else != null
            && AlwaysReturns(i.Then)
            && StmtAlwaysReturns(i.Else),
        BoundSwitch sw => sw.Cases.Any(c => c.Pattern == null) // has default case
            && sw.Cases.All(c => c.Body.Any(StmtAlwaysReturns)),
        BoundTryCatch tc => AlwaysReturns(tc.TryBody),
        _ => false,
    };

    // ── Definite assignment analysis ─────────────────────────────────────────

    /// Checks that all local variables declared without initializer are assigned
    /// before being read. Reports E0407 for violations.
    ///
    /// `outParams` (spec: define-ref-out-in-parameters, Decisions 5/6): list of
    /// parameter names that must be definitely assigned on every normal-return
    /// path. Non-out params are pre-marked as assigned (caller provides their
    /// value); `out` params start uninitialized and must be assigned by
    /// fall-through end-of-body if it is reachable.
    internal static void CheckDefiniteAssignment(
        BoundBlock block, DiagnosticBag diags,
        IReadOnlyList<Param>? functionParams = null)
    {
        var uninitLocals = new HashSet<string>();
        var assigned = new HashSet<string>();
        if (functionParams is not null)
        {
            foreach (var p in functionParams)
            {
                if (p.Modifier == ParamModifier.Out)
                    uninitLocals.Add(p.Name);
                else
                    assigned.Add(p.Name);
            }
        }
        AnalyzeBlock(block, uninitLocals, assigned, diags);

        // Callee-side DA for `out` (spec Decision 6): every `out` param must be
        // in the merged `assigned` set after block analysis. AnalyzeIf /
        // AnalyzeSwitch correctly union assignments from non-terminating
        // branches (throw / return paths skip the intersection requirement),
        // so `assigned` here represents "definitely assigned along every
        // normal-completing path through the body".
        if (functionParams is not null)
        {
            foreach (var p in functionParams)
            {
                if (p.Modifier == ParamModifier.Out
                    && uninitLocals.Contains(p.Name)
                    && !assigned.Contains(p.Name))
                {
                    diags.Error(DiagnosticCodes.UninitializedVariable,
                        $"`out` parameter `{p.Name}` must be assigned before normal return",
                        p.Span);
                }
            }
        }
    }

    private static void AnalyzeBlock(
        BoundBlock block, HashSet<string> uninitLocals, HashSet<string> assigned,
        DiagnosticBag diags)
    {
        foreach (var stmt in block.Stmts)
            AnalyzeStmt(stmt, uninitLocals, assigned, diags);
    }

    private static void AnalyzeStmt(
        BoundStmt stmt, HashSet<string> uninitLocals, HashSet<string> assigned,
        DiagnosticBag diags)
    {
        switch (stmt)
        {
            case BoundVarDecl v:
                if (v.Init != null)
                    CheckReads(v.Init, uninitLocals, assigned, diags);
                else
                    uninitLocals.Add(v.Name);
                break;

            case BoundExprStmt e:
                CheckReads(e.Expr, uninitLocals, assigned, diags);
                CollectAssignments(e.Expr, uninitLocals, assigned);
                break;

            case BoundReturn r:
                if (r.Value != null) CheckReads(r.Value, uninitLocals, assigned, diags);
                break;

            case BoundThrow th:
                CheckReads(th.Value, uninitLocals, assigned, diags);
                break;

            case BoundIf i:
                AnalyzeIf(i, uninitLocals, assigned, diags);
                break;

            case BoundBlockStmt b:
                AnalyzeBlock(b.Block, uninitLocals, assigned, diags);
                break;

            case BoundWhile w:
                CheckReads(w.Cond, uninitLocals, assigned, diags);
                var loopAssigned = new HashSet<string>(assigned);
                AnalyzeBlock(w.Body, uninitLocals, loopAssigned, diags);
                break;

            case BoundDoWhile dw:
                AnalyzeBlock(dw.Body, uninitLocals, assigned, diags);
                CheckReads(dw.Cond, uninitLocals, assigned, diags);
                break;

            case BoundFor f:
                AnalyzeFor(f, uninitLocals, assigned, diags);
                break;

            case BoundForeach fe:
                CheckReads(fe.Collection, uninitLocals, assigned, diags);
                var feAssigned = new HashSet<string>(assigned);
                AnalyzeBlock(fe.Body, uninitLocals, feAssigned, diags);
                break;

            case BoundSwitch sw:
                AnalyzeSwitch(sw, uninitLocals, assigned, diags);
                break;

            case BoundTryCatch tc:
                AnalyzeTryCatch(tc, uninitLocals, assigned, diags);
                break;

            case BoundBreak or BoundContinue:
                break;
        }
    }

    private static void AnalyzeIf(
        BoundIf i, HashSet<string> uninitLocals, HashSet<string> assigned,
        DiagnosticBag diags)
    {
        CheckReads(i.Cond, uninitLocals, assigned, diags);
        var thenAssigned = new HashSet<string>(assigned);
        AnalyzeBlock(i.Then, uninitLocals, thenAssigned, diags);
        if (i.Else != null)
        {
            var elseAssigned = new HashSet<string>(assigned);
            AnalyzeStmt(i.Else, uninitLocals, elseAssigned, diags);

            // Spec define-ref-out-in-parameters Decision 6 / scenario "throw
            // 路径不算赋值"：if a branch always exits via return/throw, only
            // the other branch's assignments propagate. Otherwise intersect
            // (must be assigned on both paths).
            bool thenTerminates = AlwaysReturns(i.Then);
            bool elseTerminates = StmtAlwaysReturns(i.Else);
            if (thenTerminates && !elseTerminates)
                assigned.UnionWith(elseAssigned);
            else if (!thenTerminates && elseTerminates)
                assigned.UnionWith(thenAssigned);
            else if (!thenTerminates && !elseTerminates)
                foreach (var name in thenAssigned)
                    if (elseAssigned.Contains(name)) assigned.Add(name);
            // both terminate → control never falls through; leave `assigned` alone
        }
        else
        {
            // No else branch: control may fall through if condition is false,
            // so assignments inside `then` aren't guaranteed. Don't propagate.
        }
    }

    private static void AnalyzeFor(
        BoundFor f, HashSet<string> uninitLocals, HashSet<string> assigned,
        DiagnosticBag diags)
    {
        if (f.Init != null) AnalyzeStmt(f.Init, uninitLocals, assigned, diags);
        if (f.Cond != null) CheckReads(f.Cond, uninitLocals, assigned, diags);
        var forAssigned = new HashSet<string>(assigned);
        AnalyzeBlock(f.Body, uninitLocals, forAssigned, diags);
        if (f.Increment != null) CheckReads(f.Increment, uninitLocals, forAssigned, diags);
    }

    private static void AnalyzeSwitch(
        BoundSwitch sw, HashSet<string> uninitLocals, HashSet<string> assigned,
        DiagnosticBag diags)
    {
        CheckReads(sw.Subject, uninitLocals, assigned, diags);
        bool hasDefault = sw.Cases.Any(c => c.Pattern == null);
        HashSet<string>? commonAssigned = null;
        foreach (var c in sw.Cases)
        {
            if (c.Pattern != null) CheckReads(c.Pattern, uninitLocals, assigned, diags);
            var caseAssigned = new HashSet<string>(assigned);
            foreach (var s in c.Body) AnalyzeStmt(s, uninitLocals, caseAssigned, diags);
            if (commonAssigned == null)
                commonAssigned = new HashSet<string>(caseAssigned);
            else
                commonAssigned.IntersectWith(caseAssigned);
        }
        if (hasDefault && commonAssigned != null)
            assigned.UnionWith(commonAssigned);
    }

    private static void AnalyzeTryCatch(
        BoundTryCatch tc, HashSet<string> uninitLocals, HashSet<string> assigned,
        DiagnosticBag diags)
    {
        AnalyzeBlock(tc.TryBody, uninitLocals, assigned, diags);
        foreach (var clause in tc.Catches)
            AnalyzeBlock(clause.Body, uninitLocals, new HashSet<string>(assigned), diags);
        if (tc.Finally != null)
            AnalyzeBlock(tc.Finally, uninitLocals, assigned, diags);
    }

    // ── Expression read checking ─────────────────────────────────────────────

    private static void CheckReads(
        BoundExpr expr, HashSet<string> uninitLocals, HashSet<string> assigned,
        DiagnosticBag diags)
    {
        switch (expr)
        {
            case BoundIdent id:
                if (uninitLocals.Contains(id.Name) && !assigned.Contains(id.Name))
                    diags.Error(DiagnosticCodes.UninitializedVariable,
                        $"variable `{id.Name}` may be used before being assigned",
                        id.Span);
                break;
            case BoundBinary b:
                CheckReads(b.Left, uninitLocals, assigned, diags);
                CheckReads(b.Right, uninitLocals, assigned, diags);
                break;
            case BoundUnary u:
                CheckReads(u.Operand, uninitLocals, assigned, diags);
                break;
            case BoundPostfix p:
                CheckReads(p.Operand, uninitLocals, assigned, diags);
                break;
            case BoundCall c:
                if (c.Receiver != null) CheckReads(c.Receiver, uninitLocals, assigned, diags);
                foreach (var a in c.Args)
                {
                    // Spec define-ref-out-in-parameters Decision 5: caller-side
                    // DA for `out` arguments. The target lvalue (or `out var x`
                    // freshly declared local) is initialized by the call's
                    // post-condition, not by being read. Skip CheckReads on it
                    // and mark it assigned.
                    if (a is BoundModifiedArg
                        { Modifier: Z42.Syntax.Parser.ArgModifier.Out } bma)
                    {
                        if (bma.Inner is BoundIdent outId)
                        {
                            if (uninitLocals.Contains(outId.Name))
                                assigned.Add(outId.Name);
                        }
                        else
                        {
                            // For `ref obj.f` / `ref a[i]`, recurse so the target
                            // expression itself (obj / a) is checked but the
                            // location's prior content is not required.
                            CheckReadsForRefTarget(bma.Inner, uninitLocals, assigned, diags);
                        }
                        continue;
                    }
                    // For ref / in: target must be already initialized to be passed.
                    // Standard CheckReads already handles this via BoundIdent rule.
                    CheckReads(a, uninitLocals, assigned, diags);
                }
                break;
            case BoundAssign a:
                CheckReads(a.Value, uninitLocals, assigned, diags);
                if (a.Target is BoundIdent tid && uninitLocals.Contains(tid.Name))
                    assigned.Add(tid.Name);
                break;
            case BoundConditional t:
                CheckReads(t.Cond, uninitLocals, assigned, diags);
                CheckReads(t.Then, uninitLocals, assigned, diags);
                CheckReads(t.Else, uninitLocals, assigned, diags);
                break;
            case BoundNullCoalesce nc:
                CheckReads(nc.Left, uninitLocals, assigned, diags);
                CheckReads(nc.Right, uninitLocals, assigned, diags);
                break;
            case BoundNullConditional nc:
                CheckReads(nc.Target, uninitLocals, assigned, diags);
                break;
            case BoundMember m:
                CheckReads(m.Target, uninitLocals, assigned, diags);
                break;
            case BoundIndex ix:
                CheckReads(ix.Target, uninitLocals, assigned, diags);
                CheckReads(ix.Index, uninitLocals, assigned, diags);
                break;
            case BoundCast c:
                CheckReads(c.Operand, uninitLocals, assigned, diags);
                break;
            case BoundNew n:
                foreach (var a in n.Args) CheckReads(a, uninitLocals, assigned, diags);
                break;
            case BoundArrayCreate ac:
                CheckReads(ac.Size, uninitLocals, assigned, diags);
                break;
            case BoundArrayLit al:
                foreach (var e in al.Elements) CheckReads(e, uninitLocals, assigned, diags);
                break;
            case BoundInterpolatedStr interp:
                foreach (var part in interp.Parts)
                    if (part is BoundExprPart ep)
                        CheckReads(ep.Inner, uninitLocals, assigned, diags);
                break;
            case BoundIsPattern ip:
                CheckReads(ip.Target, uninitLocals, assigned, diags);
                break;
            case BoundSwitchExpr sw:
                CheckReads(sw.Subject, uninitLocals, assigned, diags);
                foreach (var arm in sw.Arms)
                {
                    if (arm.Pattern != null) CheckReads(arm.Pattern, uninitLocals, assigned, diags);
                    CheckReads(arm.Body, uninitLocals, assigned, diags);
                }
                break;
        }
    }

    private static void CollectAssignments(
        BoundExpr expr, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        if (expr is BoundAssign { Target: BoundIdent id }
            && uninitLocals.Contains(id.Name))
            assigned.Add(id.Name);
    }

    /// For `ref obj.f` / `ref a[i]` style `out` args: still need the *container*
    /// (obj / a) to be initialized to compute the address; the field/element
    /// itself doesn't need a prior value.
    private static void CheckReadsForRefTarget(
        BoundExpr expr, HashSet<string> uninitLocals, HashSet<string> assigned,
        DiagnosticBag diags)
    {
        switch (expr)
        {
            case BoundIndex ix:
                CheckReads(ix.Target, uninitLocals, assigned, diags);
                CheckReads(ix.Index, uninitLocals, assigned, diags);
                break;
            case BoundMember m:
                CheckReads(m.Target, uninitLocals, assigned, diags);
                break;
            // BoundIdent already short-circuited in caller for `out var x` case;
            // any other shape isn't a valid ref target (TypeChecker rejects).
            default:
                CheckReads(expr, uninitLocals, assigned, diags);
                break;
        }
    }
}
