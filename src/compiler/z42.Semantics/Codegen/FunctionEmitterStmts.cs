using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Bound statement and control flow emission — part of FunctionEmitter.
internal sealed partial class FunctionEmitter
{
    // ── Block ─────────────────────────────────────────────────────────────────

    private void EmitBoundBlock(BoundBlock block)
    {
        // Sibling pre-pass: register every BoundLocalFunction's lifted name
        // BEFORE emitting any stmt so that mutually-referencing siblings and
        // direct recursion resolve correctly inside lifted bodies.
        // See impl-local-fn-l2 design Decision 7.
        foreach (var stmt in block.Stmts)
        {
            if (stmt is BoundLocalFunction lfn
                && !_localFnLiftedNames.ContainsKey(lfn.Name))
            {
                _localFnLiftedNames[lfn.Name] = $"{_currentFnQualName}__{lfn.Name}";
            }
        }

        foreach (var stmt in block.Stmts)
        {
            if (_blockEnded) break;
            EmitBoundStmt(stmt);
        }
    }

    // ── Statements ────────────────────────────────────────────────────────────

    /// Lazy-instantiated stmt visitor (nested class below). Per-emitter
    /// instance so it can hold a reference to `this` for outer access.
    private IrEmitStmtVisitor? _stmtVisitor;

    private void EmitBoundStmt(BoundStmt stmt)
    {
        TrackLine(stmt.Span);
        (_stmtVisitor ??= new IrEmitStmtVisitor(this)).Visit(stmt);
    }

    /// introduce-bound-visitor S6 — replaces the legacy switch in
    /// `EmitBoundStmt`. Nested private class so it accesses FunctionEmitter's
    /// private state through `_e` without leaking visibility. Behavior
    /// identical: each Visit method either calls a partial helper
    /// (Emit{Bound,If,While,...}) or emits inline.
    private sealed class IrEmitStmtVisitor : BoundStmtVisitor<Unit>
    {
        private readonly FunctionEmitter _e;
        public IrEmitStmtVisitor(FunctionEmitter e) { _e = e; }

        protected override Unit VisitVarDecl(BoundVarDecl v)
        {
            if (v.Init != null)
            {
                var reg = _e.EmitExpr(v.Init);
                // WriteBackName will allocate a register for this variable on
                // first assignment.
                _e.WriteBackName(v.Name, reg);
            }
            // If no initializer, variable will get a register on first assignment.
            return default;
        }

        protected override Unit VisitReturn(BoundReturn r)
        {
            _e.EndBlock(r.Value != null
                ? new RetTerm(_e.EmitExpr(r.Value))
                : new RetTerm(null));
            return default;
        }

        protected override Unit VisitExprStmt(BoundExprStmt e)
        {
            _e.EmitExpr(e.Expr);
            return default;
        }

        protected override Unit VisitBlockStmt(BoundBlockStmt b)
        {
            _e.EmitBoundBlock(b.Block);
            return default;
        }

        protected override Unit VisitIf(BoundIf i)            { _e.EmitBoundIf(i); return default; }
        protected override Unit VisitWhile(BoundWhile w)      { _e.EmitBoundWhile(w); return default; }
        protected override Unit VisitDoWhile(BoundDoWhile dw) { _e.EmitBoundDoWhile(dw); return default; }
        protected override Unit VisitFor(BoundFor f)          { _e.EmitBoundFor(f); return default; }
        protected override Unit VisitForeach(BoundForeach fe) { _e.EmitBoundForeach(fe); return default; }

        protected override Unit VisitBreak(BoundBreak br)
        {
            if (_e._loopStack.Count > 0)
                _e.EndBlock(new BrTerm(_e._loopStack.Peek().Break));
            return default;
        }

        protected override Unit VisitContinue(BoundContinue co)
        {
            if (_e._loopStack.Count > 0)
                _e.EndBlock(new BrTerm(_e._loopStack.Peek().Continue));
            return default;
        }

        protected override Unit VisitSwitch(BoundSwitch sw)         { _e.EmitBoundSwitchStmt(sw); return default; }
        protected override Unit VisitTryCatch(BoundTryCatch tc)     { _e.EmitBoundTryCatch(tc); return default; }

        protected override Unit VisitThrow(BoundThrow th)
        {
            _e.EndBlock(new ThrowTerm(_e.EmitExpr(th.Value)));
            return default;
        }

        protected override Unit VisitPinned(BoundPinned p)                     { _e.EmitBoundPinned(p); return default; }
        protected override Unit VisitLocalFunction(BoundLocalFunction lfn)     { _e.EmitBoundLocalFunction(lfn); return default; }
    }

    /// Local-function declaration — emit a lifted module-level function named
    /// `<Owner>__<LocalName>`. Two paths depending on captures:
    ///   • No captures (L2)  → lift body, leave mapping for direct `Call` at
    ///     subsequent call sites (handled in `EmitBoundCall.Free`).
    ///   • Has captures (L3) → lift body with env param; emit a `MkClos` here
    ///     into a register holding the closure value, and override the
    ///     mapping in `_localFnLiftedNames` to that register (call sites then
    ///     emit `CallIndirect`).
    /// See docs/design/language/closure.md §3.4 + impl-closure-l3-core Decision 9.
    private void EmitBoundLocalFunction(BoundLocalFunction lfn)
    {
        var liftedName = _localFnLiftedNames[lfn.Name];

        if (lfn.Captures.Count == 0)
        {
            // L2 path: lifted body without env; static call.
            var subEmitter = new FunctionEmitter(_ctx, _localFnLiftedNames);
            var lifted     = subEmitter.EmitLiftedLocalFunction(liftedName, lfn);
            _ctx.RegisterLiftedFunction(lifted);
            return;
        }

        // L3 path: lifted body with env at reg 0; create a Closure value at
        // declaration site and stash it in a register. Call sites that resolve
        // to this name need to switch to indirect call via the closure reg.
        var captureRegs = lfn.Captures.Select(c => EmitCaptureExpr(c)).ToList();
        var subEmitter2 = new FunctionEmitter(_ctx, _localFnLiftedNames);
        var lifted2     = subEmitter2.EmitLiftedLocalFunctionWithEnv(liftedName, lfn);
        _ctx.RegisterLiftedFunction(lifted2);

        var closureReg = Alloc(IrType.Ref);
        Emit(new MkClosInstr(closureReg, liftedName, captureRegs));
        // Bind the local-fn name to the closure reg so subsequent calls in
        // this function emit CallIndirect rather than static Call.
        _locals[lfn.Name] = closureReg;
        // Remove the L2 lifted-name mapping so EmitBoundCall.Free falls back
        // to the indirect-call path for this name.
        _localFnLiftedNames.Remove(lfn.Name);
    }

    /// Spec C5 — `pinned p = s { body }`:
    ///   PinPtr   <view>, <source>
    ///   <body>
    ///   UnpinPtr <view>
    /// `p.ptr` / `p.len` inside the body lower to the standard FieldGet
    /// IR, dispatched at runtime against `Value::PinnedView` (C4).
    private void EmitBoundPinned(BoundPinned p)
    {
        var srcReg = EmitExpr(p.Source);
        var viewReg = Alloc(IrType.Ref);
        Emit(new PinPtrInstr(viewReg, srcReg));

        // Bind the user-visible name (`p`) to the view register so any
        // `p.ptr` / `p.len` inside the body resolves to FieldGet on it.
        WriteBackName(p.Name, viewReg);

        EmitBoundBlock(p.Body);

        // The TypeChecker forbids early control flow inside the block, so
        // every `pin` reaches a single `unpin` at the end of straight-line
        // emission. (See spec C5 — control-flow restrictions.)
        if (!_blockEnded)
            Emit(new UnpinPtrInstr(viewReg));
    }

    // ── Control flow ──────────────────────────────────────────────────────────

    private void EmitBoundIf(BoundIf ifStmt)
    {
        var condReg    = EmitExpr(ifStmt.Cond);
        string thenLbl = FreshLabel("then");
        string elseLbl = FreshLabel(ifStmt.Else != null ? "else" : "end");
        string endLbl  = ifStmt.Else != null ? FreshLabel("end") : elseLbl;

        EndBlock(new BrCondTerm(condReg, thenLbl, elseLbl));

        StartBlock(thenLbl);
        EmitBoundBlock(ifStmt.Then);
        if (!_blockEnded) EndBlock(new BrTerm(endLbl));

        if (ifStmt.Else != null)
        {
            StartBlock(elseLbl);
            EmitBoundStmt(ifStmt.Else);
            if (!_blockEnded) EndBlock(new BrTerm(endLbl));
        }

        StartBlock(endLbl);
    }

}
