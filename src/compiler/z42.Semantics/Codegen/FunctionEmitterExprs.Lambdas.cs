using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

internal sealed partial class FunctionEmitter
{

    // ── Lambda literal (impl-lambda-l2) ───────────────────────────────────────

    /// Lambda literal lowering. Two paths depending on captures:
    ///   • No captures (L2)  → lift body, emit `LoadFn`. Result is `FuncRef`.
    ///   • Has captures (L3) → lift body with env param, emit `MkClos` with
    ///     capture regs collected from the current scope. Result is `Closure`.
    /// See docs/design/closure.md §6 + impl-closure-l3-core design Decision 7/8.
    private TypedReg EmitLambdaLiteral(BoundLambda lambda)
    {
        var index    = _ctx.NextLambdaIndex(_currentFnQualName);
        var liftedNm = $"{_currentFnQualName}__lambda_{index}";

        if (lambda.Captures.Count == 0)
        {
            // L2 no-capture path (unchanged).
            var lifted = new FunctionEmitter(_ctx).EmitLifted(liftedNm, lambda);
            _ctx.RegisterLiftedFunction(lifted);

            var dstNoCap = Alloc(IrType.Ref);
            Emit(new LoadFnInstr(dstNoCap, liftedNm));
            return dstNoCap;
        }

        // L3 capture path: collect capture regs from the current scope, then
        // lift the body with an env parameter at reg 0.
        var captureRegs = lambda.Captures.Select(c => EmitCaptureExpr(c)).ToList();
        var liftedCap   = new FunctionEmitter(_ctx, _localFnLiftedNames)
                              .EmitLiftedLambdaWithEnv(liftedNm, lambda);
        _ctx.RegisterLiftedFunction(liftedCap);

        var dst = Alloc(IrType.Ref);
        // 2026-05-02 impl-closure-l3-escape-stack: 查 SemanticModel.StackAllocClosures
        // 决定 MkClosInstr.StackAlloc。escape 分析器在所有 body 绑定后写入该集合
        //（reference-equality keyed），default false（保守 fallback heap）。
        bool stackAlloc = _ctx.SemanticModel.StackAllocClosures.Contains(lambda);
        Emit(new MkClosInstr(dst, liftedNm, captureRegs, stackAlloc));
        return dst;
    }

    /// Resolve a captured variable's value to a register in the current scope.
    /// Two cases:
    ///   • The capture is a regular local in the current emitter scope (e.g. the
    ///     outer function's locals or `_locals` map) → reuse that register.
    ///   • We're emitting from inside a capturing-lifted body and the capture
    ///     refers to a variable from the *outer closure's* env → emit a chained
    ///     `ArrayGet env_reg, _envCaptureIndex(name)` to load it transitively.
    /// See impl-closure-l3-core design Decision 6 (nested captures).
    private TypedReg EmitCaptureExpr(BoundCapture c)
    {
        if (_locals.TryGetValue(c.Name, out var reg)) return reg;
        if (_envReg >= 0 && _envCaptureIndex.TryGetValue(c.Name, out var idx))
        {
            // Emit env[idx] via constant index + ArrayGet.
            var idxReg = Alloc(IrType.I32);
            Emit(new ConstI32Instr(idxReg, idx));
            var dst = Alloc(ToIrType(c.Type));
            Emit(new ArrayGetInstr(dst, new TypedReg(_envReg, IrType.Ref), idxReg));
            return dst;
        }
        throw new InvalidOperationException(
            $"capture `{c.Name}` is neither a local nor a known env entry — captures must be visible in the enclosing emitter scope");
    }

    /// Emit a capturing lambda body as a lifted IrFunction whose first
    /// register (reg 0) holds the heap-allocated env (Vec<Value>). User
    /// parameters start at reg 1. `BoundCapturedIdent` references inside
    /// the body are lowered to `ArrayGet env_reg=0, capture_index`.
    /// See impl-closure-l3-core design Decision 7.
    internal IrFunction EmitLiftedLambdaWithEnv(string liftedQualName, BoundLambda lambda)
    {
        _currentClassName = null;
        _currentFnQualName = liftedQualName;
        _sourceFile = lambda.Span.File;
        _envReg = 0;
        // Populate the env capture-index map so nested lambdas inside this
        // body can resolve their own captures via this body's env.
        _envCaptureIndex.Clear();
        for (int i = 0; i < lambda.Captures.Count; i++)
            _envCaptureIndex[lambda.Captures[i].Name] = i;

        // Reg 0 = env; user params from reg 1 onward.
        _nextReg = 1 + lambda.Params.Count;

        StartBlock("entry");
        for (int i = 0; i < lambda.Params.Count; i++)
            _locals[lambda.Params[i].Name] =
                new TypedReg(1 + i, ToIrType(lambda.Params[i].Type));

        switch (lambda.Body)
        {
            case BoundLambdaExprBody eb:
            {
                var resultReg = EmitExpr(eb.Expr);
                if (lambda.FuncType.Ret == Z42Type.Void) EndBlock(new RetTerm(null));
                else                                     EndBlock(new RetTerm(resultReg));
                break;
            }
            case BoundLambdaBlockBody bb:
                EmitBoundBlock(bb.Block);
                if (!_blockEnded) EndBlock(new RetTerm(null));
                break;
        }

        var retName  = lambda.FuncType.Ret == Z42Type.Void
            ? "void"
            : lambda.FuncType.Ret.ToString() ?? "object";
        var lineTbl  = _lineTable.Count > 0 ? _lineTable : null;
        var localTbl = SnapshotLocalVarTable();
        // ParamCount includes env (+1).
        return new IrFunction(liftedQualName, 1 + lambda.Params.Count, retName,
            "Interp", _blocks, null, MaxReg: _nextReg,
            LineTable: lineTbl, LocalVarTable: localTbl);
    }

    /// L3 capturing local-function lifting: same as `EmitLiftedLocalFunction`
    /// but with `env` reserved at reg 0 (as for capturing lambdas). User
    /// parameters start at reg 1; `BoundCapturedIdent` references inside the
    /// body lower to `ArrayGet env_reg=0, capture_index`.
    /// See impl-closure-l3-core design Decision 9.
    internal IrFunction EmitLiftedLocalFunctionWithEnv(
        string liftedQualName, BoundLocalFunction lfn)
    {
        _currentClassName = null;
        _currentFnQualName = liftedQualName;
        _sourceFile = lfn.Span.File;
        _envReg = 0;
        _envCaptureIndex.Clear();
        for (int i = 0; i < lfn.Captures.Count; i++)
            _envCaptureIndex[lfn.Captures[i].Name] = i;

        _nextReg = 1 + lfn.ParamNames.Count;

        StartBlock("entry");
        for (int i = 0; i < lfn.ParamNames.Count; i++)
            _locals[lfn.ParamNames[i]] = new TypedReg(1 + i, ToIrType(lfn.ParamTypes[i]));

        EmitBoundBlock(lfn.Body);
        if (!_blockEnded) EndBlock(new RetTerm(null));

        var retName  = lfn.RetType == Z42Type.Void ? "void" : lfn.RetType.ToString() ?? "object";
        var lineTbl  = _lineTable.Count > 0 ? _lineTable : null;
        var localTbl = SnapshotLocalVarTable();
        return new IrFunction(liftedQualName, 1 + lfn.ParamNames.Count, retName,
            "Interp", _blocks, null, MaxReg: _nextReg,
            LineTable: lineTbl, LocalVarTable: localTbl);
    }

    /// L2 local function lifting (impl-local-fn-l2): emit a `BoundLocalFunction`
    /// as a standalone module-level IrFunction. Naming and emit pattern mirror
    /// `EmitLifted` for lambdas; the difference is that local fn callers know
    /// the lifted name statically and emit `Call` (not `LoadFn`/`CallIndirect`).
    internal IrFunction EmitLiftedLocalFunction(string liftedQualName, BoundLocalFunction lfn)
    {
        _currentClassName = null;
        _currentFnQualName = liftedQualName;
        _sourceFile = lfn.Span.File;
        _nextReg = lfn.ParamNames.Count;

        StartBlock("entry");
        for (int i = 0; i < lfn.ParamNames.Count; i++)
            _locals[lfn.ParamNames[i]] = new TypedReg(i, ToIrType(lfn.ParamTypes[i]));

        EmitBoundBlock(lfn.Body);
        if (!_blockEnded) EndBlock(new RetTerm(null));

        var retName  = lfn.RetType == Z42Type.Void ? "void" : lfn.RetType.ToString() ?? "object";
        var lineTbl  = _lineTable.Count > 0 ? _lineTable : null;
        var localTbl = SnapshotLocalVarTable();
        return new IrFunction(liftedQualName, lfn.ParamNames.Count, retName,
            "Interp", _blocks, null, MaxReg: _nextReg, LineTable: lineTbl, LocalVarTable: localTbl);
    }

    /// Emit a lifted lambda body as a standalone IrFunction. Mirrors the
    /// shape of `EmitFunction` but takes its params/body from a `BoundLambda`.
    internal IrFunction EmitLifted(string liftedQualName, BoundLambda lambda)
    {
        _currentClassName = null;
        _currentFnQualName = liftedQualName;
        _sourceFile = lambda.Span.File;
        _nextReg = lambda.Params.Count;

        StartBlock("entry");
        for (int i = 0; i < lambda.Params.Count; i++)
            _locals[lambda.Params[i].Name] = new TypedReg(i, ToIrType(lambda.Params[i].Type));

        switch (lambda.Body)
        {
            case BoundLambdaExprBody eb:
            {
                var resultReg = EmitExpr(eb.Expr);
                if (lambda.FuncType.Ret == Z42Type.Void)
                    EndBlock(new RetTerm(null));
                else
                    EndBlock(new RetTerm(resultReg));
                break;
            }
            case BoundLambdaBlockBody bb:
            {
                EmitBoundBlock(bb.Block);
                if (!_blockEnded) EndBlock(new RetTerm(null));
                break;
            }
        }

        var retName  = lambda.FuncType.Ret == Z42Type.Void ? "void" : lambda.FuncType.Ret.ToString() ?? "object";
        var lineTbl  = _lineTable.Count > 0 ? _lineTable : null;
        var localTbl = SnapshotLocalVarTable();
        return new IrFunction(liftedQualName, lambda.Params.Count, retName,
            "Interp", _blocks, null, MaxReg: _nextReg, LineTable: lineTbl, LocalVarTable: localTbl);
    }
}
