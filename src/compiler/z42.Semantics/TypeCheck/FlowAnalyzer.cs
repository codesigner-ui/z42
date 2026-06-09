using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Post-binding flow analysis passes — reachability and definite assignment.
/// Operates on already-bound BoundBlocks; no type-checking state needed.
/// Implements <see cref="IFlowAnalyzer"/> for testability; static methods retained for convenience.
///
/// introduce-bound-visitor (2026-05-10): switch dispatch on BoundStmt /
/// BoundExpr migrated to BoundStmtVisitor / BoundExprVisitor subclasses
/// (`AlwaysReturnsVisitor`, `DefiniteAssignmentVisitor`, `ReadsVisitor`).
/// Behavior preserved exactly — visitor methods enumerate every concrete
/// BoundXxx leaf, with explicit no-op overrides where the legacy switch
/// fell through to default.
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
        var v = AlwaysReturnsVisitor.Instance;
        foreach (var stmt in block.Stmts)
            if (v.Visit(stmt)) return true;
        return false;
    }

    private static bool StmtAlwaysReturns(BoundStmt stmt) =>
        AlwaysReturnsVisitor.Instance.Visit(stmt);

    private sealed class AlwaysReturnsVisitor : BoundStmtVisitor<bool>
    {
        public static readonly AlwaysReturnsVisitor Instance = new();

        // ── Always-return cases ───────────────────────────────────────────────
        protected override bool VisitReturn(BoundReturn r) => true;
        protected override bool VisitThrow(BoundThrow th)  => true;
        protected override bool VisitBlockStmt(BoundBlockStmt b)
            => AlwaysReturns(b.Block);
        protected override bool VisitIf(BoundIf i)
            => i.Else != null
            && AlwaysReturns(i.Then)
            && Visit(i.Else);
        protected override bool VisitSwitch(BoundSwitch sw)
            => sw.Cases.Any(c => c.Pattern == null)            // has default case
            && sw.Cases.All(c => c.Body.Any(StmtAlwaysReturns));
        protected override bool VisitTryCatch(BoundTryCatch tc)
            => AlwaysReturns(tc.TryBody);

        // ── Never-return cases (legacy switch's default → false) ──────────────
        protected override bool VisitVarDecl(BoundVarDecl v)              => false;
        protected override bool VisitExprStmt(BoundExprStmt e)            => false;
        protected override bool VisitWhile(BoundWhile w)                  => false;
        protected override bool VisitDoWhile(BoundDoWhile dw)             => false;
        protected override bool VisitFor(BoundFor f)                      => false;
        protected override bool VisitForeach(BoundForeach fe)             => false;
        protected override bool VisitBreak(BoundBreak br)                 => false;
        protected override bool VisitContinue(BoundContinue co)           => false;
        protected override bool VisitLocalFunction(BoundLocalFunction lf) => false;
        protected override bool VisitPinned(BoundPinned p)                => false;
    }

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

        var visitor = new DefiniteAssignmentVisitor(uninitLocals, assigned, diags);
        visitor.AnalyzeBlock(block);

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

    /// Stateful BoundStmt visitor implementing the body of `AnalyzeStmt`.
    /// Mutable `_assigned` is swapped/cloned across branch boundaries
    /// (AnalyzeIf / AnalyzeFor / AnalyzeSwitch / AnalyzeTryCatch).
    /// Composes with `ReadsVisitor` for the BoundExpr `CheckReads` pass.
    private sealed class DefiniteAssignmentVisitor : BoundStmtVisitor<Unit>
    {
        private readonly HashSet<string> _uninit;
        private HashSet<string> _assigned;
        private readonly DiagnosticBag _diags;
        private readonly ReadsVisitor _reads;

        public DefiniteAssignmentVisitor(
            HashSet<string> uninit, HashSet<string> assigned, DiagnosticBag diags)
        {
            _uninit = uninit;
            _assigned = assigned;
            _diags = diags;
            _reads = new ReadsVisitor(this);
        }

        public HashSet<string> Assigned => _assigned;

        public void AnalyzeBlock(BoundBlock block)
        {
            foreach (var stmt in block.Stmts) Visit(stmt);
        }

        // ── Per-stmt overrides (mirror legacy AnalyzeStmt switch) ─────────────

        protected override Unit VisitVarDecl(BoundVarDecl v)
        {
            if (v.Init != null) CheckReads(v.Init);
            else _uninit.Add(v.Name);
            return default;
        }

        protected override Unit VisitExprStmt(BoundExprStmt e)
        {
            CheckReads(e.Expr);
            CollectAssignments(e.Expr);
            return default;
        }

        protected override Unit VisitReturn(BoundReturn r)
        {
            if (r.Value != null) CheckReads(r.Value);
            return default;
        }

        protected override Unit VisitThrow(BoundThrow th)
        {
            CheckReads(th.Value);
            return default;
        }

        protected override Unit VisitIf(BoundIf i)
        {
            CheckReads(i.Cond);
            var prev = _assigned;
            var thenAssigned = new HashSet<string>(prev);
            _assigned = thenAssigned;
            AnalyzeBlock(i.Then);

            if (i.Else != null)
            {
                var elseAssigned = new HashSet<string>(prev);
                _assigned = elseAssigned;
                Visit(i.Else);

                // Spec define-ref-out-in-parameters Decision 6 / scenario "throw
                // 路径不算赋值"：if a branch always exits via return/throw, only
                // the other branch's assignments propagate. Otherwise intersect
                // (must be assigned on both paths).
                bool thenTerminates = AlwaysReturns(i.Then);
                bool elseTerminates = StmtAlwaysReturns(i.Else);
                _assigned = prev;
                if (thenTerminates && !elseTerminates)
                    _assigned.UnionWith(elseAssigned);
                else if (!thenTerminates && elseTerminates)
                    _assigned.UnionWith(thenAssigned);
                else if (!thenTerminates && !elseTerminates)
                    foreach (var name in thenAssigned)
                        if (elseAssigned.Contains(name)) _assigned.Add(name);
                // both terminate → control never falls through; leave alone
            }
            else
            {
                // No else branch: control may fall through if condition is false,
                // so assignments inside `then` aren't guaranteed. Don't propagate.
                _assigned = prev;
            }
            return default;
        }

        protected override Unit VisitBlockStmt(BoundBlockStmt b)
        {
            AnalyzeBlock(b.Block);
            return default;
        }

        protected override Unit VisitWhile(BoundWhile w)
        {
            CheckReads(w.Cond);
            var prev = _assigned;
            _assigned = new HashSet<string>(prev);
            AnalyzeBlock(w.Body);
            _assigned = prev;
            return default;
        }

        protected override Unit VisitDoWhile(BoundDoWhile dw)
        {
            AnalyzeBlock(dw.Body);
            CheckReads(dw.Cond);
            return default;
        }

        protected override Unit VisitFor(BoundFor f)
        {
            if (f.Init != null) Visit(f.Init);
            if (f.Cond != null) CheckReads(f.Cond);
            var prev = _assigned;
            var forAssigned = new HashSet<string>(prev);
            _assigned = forAssigned;
            AnalyzeBlock(f.Body);
            if (f.Increment != null) CheckReads(f.Increment);
            _assigned = prev;
            return default;
        }

        protected override Unit VisitForeach(BoundForeach fe)
        {
            CheckReads(fe.Collection);
            var prev = _assigned;
            _assigned = new HashSet<string>(prev);
            AnalyzeBlock(fe.Body);
            _assigned = prev;
            return default;
        }

        protected override Unit VisitSwitch(BoundSwitch sw)
        {
            CheckReads(sw.Subject);
            bool hasDefault = sw.Cases.Any(c => c.Pattern == null);
            HashSet<string>? commonAssigned = null;
            var prev = _assigned;
            foreach (var c in sw.Cases)
            {
                if (c.Pattern != null) CheckReads(c.Pattern);
                var caseAssigned = new HashSet<string>(prev);
                _assigned = caseAssigned;
                foreach (var s in c.Body) Visit(s);
                if (commonAssigned == null)
                    commonAssigned = new HashSet<string>(caseAssigned);
                else
                    commonAssigned.IntersectWith(caseAssigned);
            }
            _assigned = prev;
            if (hasDefault && commonAssigned != null)
                _assigned.UnionWith(commonAssigned);
            return default;
        }

        protected override Unit VisitTryCatch(BoundTryCatch tc)
        {
            AnalyzeBlock(tc.TryBody);
            var prev = _assigned;
            foreach (var clause in tc.Catches)
            {
                _assigned = new HashSet<string>(prev);
                AnalyzeBlock(clause.Body);
            }
            _assigned = prev;
            if (tc.Finally != null) AnalyzeBlock(tc.Finally);
            return default;
        }

        // ── Stmts the legacy switch ignored (preserve no-op) ──────────────────
        protected override Unit VisitBreak(BoundBreak br)                 => default;
        protected override Unit VisitContinue(BoundContinue co)           => default;
        protected override Unit VisitLocalFunction(BoundLocalFunction lf) => default;
        protected override Unit VisitPinned(BoundPinned p)                => default;

        // ── Internal helpers ──────────────────────────────────────────────────

        public void CheckReads(BoundExpr expr) => _reads.Visit(expr);

        public void MarkAssigned(string name)
        {
            if (_uninit.Contains(name)) _assigned.Add(name);
        }

        public bool IsUninitAndUnassigned(string name)
            => _uninit.Contains(name) && !_assigned.Contains(name);

        public void ReportUninitialized(BoundIdent id)
        {
            _diags.Error(DiagnosticCodes.UninitializedVariable,
                $"variable `{id.Name}` may be used before being assigned",
                id.Span);
        }

        private void CollectAssignments(BoundExpr expr)
        {
            if (expr is BoundAssign { Target: BoundIdent id })
                MarkAssigned(id.Name);
        }
    }

    /// Stateful BoundExpr visitor implementing the body of `CheckReads`.
    /// Mutates `_outer._assigned` via `outer.MarkAssigned`. Coverage matches
    /// the legacy switch exactly — leaves and unhandled expressions are
    /// explicit no-ops.
    private sealed class ReadsVisitor : BoundExprVisitor<Unit>
    {
        private readonly DefiniteAssignmentVisitor _outer;

        public ReadsVisitor(DefiniteAssignmentVisitor outer) { _outer = outer; }

        // ── Active cases (mirror legacy CheckReads switch) ────────────────────

        protected override Unit VisitIdent(BoundIdent id)
        {
            if (_outer.IsUninitAndUnassigned(id.Name))
                _outer.ReportUninitialized(id);
            return default;
        }

        protected override Unit VisitBinary(BoundBinary b)
        {
            Visit(b.Left); Visit(b.Right);
            return default;
        }

        protected override Unit VisitUnary(BoundUnary u)        { Visit(u.Operand); return default; }
        protected override Unit VisitPostfix(BoundPostfix p)    { Visit(p.Operand); return default; }

        protected override Unit VisitCall(BoundCall c)
        {
            if (c.Receiver != null) Visit(c.Receiver);
            foreach (var a in c.Args)
            {
                // Spec define-ref-out-in-parameters Decision 5: caller-side
                // DA for `out` arguments. The target lvalue (or `out var x`
                // freshly declared local) is initialized by the call's
                // post-condition, not by being read. Skip CheckReads on it
                // and mark it assigned.
                if (a is BoundModifiedArg
                    { Modifier: ArgModifier.Out } bma)
                {
                    if (bma.Inner is BoundIdent outId)
                    {
                        _outer.MarkAssigned(outId.Name);
                    }
                    else
                    {
                        // For `ref obj.f` / `ref a[i]`, recurse so the target
                        // expression itself (obj / a) is checked but the
                        // location's prior content is not required.
                        CheckRefTarget(bma.Inner);
                    }
                    continue;
                }
                // For ref / in: target must be already initialized to be passed.
                // Standard CheckReads already handles this via BoundIdent rule.
                Visit(a);
            }
            return default;
        }

        protected override Unit VisitAssign(BoundAssign a)
        {
            Visit(a.Value);
            if (a.Target is BoundIdent tid) _outer.MarkAssigned(tid.Name);
            return default;
        }

        protected override Unit VisitConditional(BoundConditional t)
        {
            Visit(t.Cond); Visit(t.Then); Visit(t.Else);
            return default;
        }

        protected override Unit VisitNullCoalesce(BoundNullCoalesce nc)
        {
            Visit(nc.Left); Visit(nc.Right);
            return default;
        }

        protected override Unit VisitNullConditional(BoundNullConditional nc)
        {
            Visit(nc.Target);
            return default;
        }

        protected override Unit VisitMember(BoundMember m)
        {
            Visit(m.Target);
            return default;
        }

        protected override Unit VisitIndex(BoundIndex ix)
        {
            Visit(ix.Target); Visit(ix.Index);
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

        protected override Unit VisitInterpolatedStr(BoundInterpolatedStr interp)
        {
            foreach (var part in interp.Parts)
                if (part is BoundExprPart ep) Visit(ep.Inner);
            return default;
        }

        protected override Unit VisitIsPattern(BoundIsPattern ip)
        {
            Visit(ip.Target);
            return default;
        }

        protected override Unit VisitSwitchExpr(BoundSwitchExpr sw)
        {
            Visit(sw.Subject);
            foreach (var arm in sw.Arms)
            {
                if (arm.Pattern != null) Visit(arm.Pattern);
                Visit(arm.Body);
            }
            return default;
        }

        // ── No-op cases (legacy default fall-through) ─────────────────────────
        protected override Unit VisitLitInt(BoundLitInt n)              => default;
        protected override Unit VisitLitFloat(BoundLitFloat f)          => default;
        protected override Unit VisitLitStr(BoundLitStr s)              => default;
        protected override Unit VisitLitBool(BoundLitBool b)            => default;
        protected override Unit VisitLitNull(BoundLitNull n)            => default;
        protected override Unit VisitLitChar(BoundLitChar c)            => default;
        protected override Unit VisitDefault(BoundDefault d)            => default;
        protected override Unit VisitTypeof(BoundTypeof t)              => default;
        protected override Unit VisitCapturedIdent(BoundCapturedIdent ci) => default;
        protected override Unit VisitLambda(BoundLambda l)              => default;
        protected override Unit VisitModifiedArg(BoundModifiedArg m)    => default;  // handled inside VisitCall

        protected override Unit VisitIndirectCall(BoundIndirectCall ic)
        {
            // Both the callee (function value) and args are read positions.
            Visit(ic.Callee);
            foreach (var a in ic.Args) Visit(a);
            return default;
        }
        protected override Unit VisitError(BoundError err)              => default;

        /// For `ref obj.f` / `ref a[i]` style `out` args: still need the *container*
        /// (obj / a) to be initialized to compute the address; the field/element
        /// itself doesn't need a prior value.
        private void CheckRefTarget(BoundExpr expr)
        {
            switch (expr)
            {
                case BoundIndex ix:
                    Visit(ix.Target); Visit(ix.Index);
                    break;
                case BoundMember m:
                    Visit(m.Target);
                    break;
                // BoundIdent already short-circuited in caller for `out var x` case;
                // any other shape isn't a valid ref target (TypeChecker rejects).
                default:
                    Visit(expr);
                    break;
            }
        }
    }
}
