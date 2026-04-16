using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;

namespace Z42.Semantics.TypeCheck;

/// Post-binding analysis passes — reachability and definite assignment.
public sealed partial class TypeChecker
{
    // ── Reachability analysis ────────────────────────────────────────────────

    /// Returns true if ALL control-flow paths through the block reach a return/throw.
    private static bool AlwaysReturns(BoundBlock block)
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
    private void CheckDefiniteAssignment(BoundBlock block)
    {
        // Track which uninit-declared locals have been definitely assigned.
        var uninitLocals = new HashSet<string>();
        var assigned = new HashSet<string>();
        AnalyzeBlock(block, uninitLocals, assigned);
    }

    private void AnalyzeBlock(
        BoundBlock block, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        foreach (var stmt in block.Stmts)
            AnalyzeStmt(stmt, uninitLocals, assigned);
    }

    private void AnalyzeStmt(
        BoundStmt stmt, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        switch (stmt)
        {
            case BoundVarDecl v:
                if (v.Init != null)
                    CheckReads(v.Init, uninitLocals, assigned);
                else
                    uninitLocals.Add(v.Name);  // declared without init
                break;

            case BoundExprStmt e:
                CheckReads(e.Expr, uninitLocals, assigned);
                // Check if this is an assignment that initializes an uninit local
                CollectAssignments(e.Expr, uninitLocals, assigned);
                break;

            case BoundReturn r:
                if (r.Value != null) CheckReads(r.Value, uninitLocals, assigned);
                break;

            case BoundThrow th:
                CheckReads(th.Value, uninitLocals, assigned);
                break;

            case BoundIf i:
                AnalyzeIf(i, uninitLocals, assigned);
                break;

            case BoundBlockStmt b:
                AnalyzeBlock(b.Block, uninitLocals, assigned);
                break;

            case BoundWhile w:
                CheckReads(w.Cond, uninitLocals, assigned);
                // Loop body might not execute → conservative
                var loopAssigned = new HashSet<string>(assigned);
                AnalyzeBlock(w.Body, uninitLocals, loopAssigned);
                break;

            case BoundDoWhile dw:
                // Body always executes once → body assignments ARE definite
                AnalyzeBlock(dw.Body, uninitLocals, assigned);
                CheckReads(dw.Cond, uninitLocals, assigned);
                break;

            case BoundFor f:
                AnalyzeFor(f, uninitLocals, assigned);
                break;

            case BoundForeach fe:
                CheckReads(fe.Collection, uninitLocals, assigned);
                // Loop body might not execute
                var feAssigned = new HashSet<string>(assigned);
                AnalyzeBlock(fe.Body, uninitLocals, feAssigned);
                break;

            case BoundSwitch sw:
                AnalyzeSwitch(sw, uninitLocals, assigned);
                break;

            case BoundTryCatch tc:
                AnalyzeTryCatch(tc, uninitLocals, assigned);
                break;

            case BoundBreak or BoundContinue:
                break;
        }
    }

    private void AnalyzeIf(
        BoundIf i, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        CheckReads(i.Cond, uninitLocals, assigned);
        var thenAssigned = new HashSet<string>(assigned);
        AnalyzeBlock(i.Then, uninitLocals, thenAssigned);
        if (i.Else != null)
        {
            var elseAssigned = new HashSet<string>(assigned);
            AnalyzeStmt(i.Else, uninitLocals, elseAssigned);
            // Both branches assign → definitely assigned after if-else
            foreach (var name in thenAssigned)
                if (elseAssigned.Contains(name)) assigned.Add(name);
        }
        // If no else: then-only assignments are NOT definite
    }

    private void AnalyzeFor(
        BoundFor f, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        if (f.Init != null) AnalyzeStmt(f.Init, uninitLocals, assigned);
        if (f.Cond != null) CheckReads(f.Cond, uninitLocals, assigned);
        // Loop body might not execute
        var forAssigned = new HashSet<string>(assigned);
        AnalyzeBlock(f.Body, uninitLocals, forAssigned);
        if (f.Increment != null) CheckReads(f.Increment, uninitLocals, forAssigned);
    }

    private void AnalyzeSwitch(
        BoundSwitch sw, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        CheckReads(sw.Subject, uninitLocals, assigned);
        bool hasDefault = sw.Cases.Any(c => c.Pattern == null);
        HashSet<string>? commonAssigned = null;
        foreach (var c in sw.Cases)
        {
            if (c.Pattern != null) CheckReads(c.Pattern, uninitLocals, assigned);
            var caseAssigned = new HashSet<string>(assigned);
            foreach (var s in c.Body) AnalyzeStmt(s, uninitLocals, caseAssigned);
            if (commonAssigned == null)
                commonAssigned = new HashSet<string>(caseAssigned);
            else
                commonAssigned.IntersectWith(caseAssigned);
        }
        if (hasDefault && commonAssigned != null)
            assigned.UnionWith(commonAssigned);
    }

    private void AnalyzeTryCatch(
        BoundTryCatch tc, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        AnalyzeBlock(tc.TryBody, uninitLocals, assigned);
        foreach (var clause in tc.Catches)
            AnalyzeBlock(clause.Body, uninitLocals, new HashSet<string>(assigned));
        if (tc.Finally != null)
            AnalyzeBlock(tc.Finally, uninitLocals, assigned);
    }

    // ── Expression read checking ─────────────────────────────────────────────

    /// Walk an expression tree and report errors for reads of uninitialized locals.
    private void CheckReads(
        BoundExpr expr, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        switch (expr)
        {
            case BoundIdent id:
                if (uninitLocals.Contains(id.Name) && !assigned.Contains(id.Name))
                    _diags.Error(DiagnosticCodes.UninitializedVariable,
                        $"variable `{id.Name}` may be used before being assigned",
                        id.Span);
                break;
            case BoundBinary b:
                CheckReads(b.Left, uninitLocals, assigned);
                CheckReads(b.Right, uninitLocals, assigned);
                break;
            case BoundUnary u:
                CheckReads(u.Operand, uninitLocals, assigned);
                break;
            case BoundPostfix p:
                CheckReads(p.Operand, uninitLocals, assigned);
                break;
            case BoundCall c:
                CheckReadsCall(c, uninitLocals, assigned);
                break;
            case BoundAssign a:
                CheckReadsAssign(a, uninitLocals, assigned);
                break;
            case BoundConditional t:
                CheckReads(t.Cond, uninitLocals, assigned);
                CheckReads(t.Then, uninitLocals, assigned);
                CheckReads(t.Else, uninitLocals, assigned);
                break;
            case BoundNullCoalesce nc:
                CheckReads(nc.Left, uninitLocals, assigned);
                CheckReads(nc.Right, uninitLocals, assigned);
                break;
            case BoundNullConditional nc:
                CheckReads(nc.Target, uninitLocals, assigned);
                break;
            case BoundMember m:
                CheckReads(m.Target, uninitLocals, assigned);
                break;
            case BoundIndex ix:
                CheckReads(ix.Target, uninitLocals, assigned);
                CheckReads(ix.Index, uninitLocals, assigned);
                break;
            case BoundCast c:
                CheckReads(c.Operand, uninitLocals, assigned);
                break;
            case BoundNew n:
                foreach (var a in n.Args) CheckReads(a, uninitLocals, assigned);
                break;
            case BoundArrayCreate ac:
                CheckReads(ac.Size, uninitLocals, assigned);
                break;
            case BoundArrayLit al:
                foreach (var e in al.Elements) CheckReads(e, uninitLocals, assigned);
                break;
            case BoundInterpolatedStr interp:
                foreach (var part in interp.Parts)
                    if (part is BoundExprPart ep)
                        CheckReads(ep.Inner, uninitLocals, assigned);
                break;
            case BoundIsPattern ip:
                CheckReads(ip.Target, uninitLocals, assigned);
                break;
            case BoundSwitchExpr sw:
                CheckReadsSwitchExpr(sw, uninitLocals, assigned);
                break;
            // Literals and other leaf nodes: no reads to check
        }
    }

    private void CheckReadsCall(
        BoundCall c, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        if (c.Receiver != null) CheckReads(c.Receiver, uninitLocals, assigned);
        foreach (var a in c.Args) CheckReads(a, uninitLocals, assigned);
    }

    private void CheckReadsAssign(
        BoundAssign a, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        CheckReads(a.Value, uninitLocals, assigned);
        // The target side: assignment initializes the variable
        if (a.Target is BoundIdent tid && uninitLocals.Contains(tid.Name))
            assigned.Add(tid.Name);
    }

    private void CheckReadsSwitchExpr(
        BoundSwitchExpr sw, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        CheckReads(sw.Subject, uninitLocals, assigned);
        foreach (var arm in sw.Arms)
        {
            if (arm.Pattern != null) CheckReads(arm.Pattern, uninitLocals, assigned);
            CheckReads(arm.Body, uninitLocals, assigned);
        }
    }

    /// If expr is an assignment to an uninit local, mark it as assigned.
    private static void CollectAssignments(
        BoundExpr expr, HashSet<string> uninitLocals, HashSet<string> assigned)
    {
        if (expr is BoundAssign { Target: BoundIdent id }
            && uninitLocals.Contains(id.Name))
            assigned.Add(id.Name);
    }
}
