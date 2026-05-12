using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Loop statement emission: while / do-while / for / foreach. spec
/// split-emitter-stmts-calls (2026-05-12): extracted from
/// FunctionEmitterStmts.cs to keep the main file under the 300 LOC soft
/// limit. Zero behavior change.
internal sealed partial class FunctionEmitter
{
    private void EmitBoundWhile(BoundWhile ws)
    {
        string condLbl = FreshLabel("cond");
        string bodyLbl = FreshLabel("body");
        string endLbl  = FreshLabel("end");

        EndBlock(new BrTerm(condLbl));

        StartBlock(condLbl);
        var condReg = EmitExpr(ws.Cond);
        EndBlock(new BrCondTerm(condReg, bodyLbl, endLbl));

        _loopStack.Push((endLbl, condLbl));
        StartBlock(bodyLbl);
        EmitBoundBlock(ws.Body);
        if (!_blockEnded) EndBlock(new BrTerm(condLbl));
        _loopStack.Pop();

        StartBlock(endLbl);
    }

    private void EmitBoundDoWhile(BoundDoWhile dw)
    {
        string bodyLbl = FreshLabel("do_body");
        string condLbl = FreshLabel("do_cond");
        string endLbl  = FreshLabel("do_end");

        EndBlock(new BrTerm(bodyLbl));

        _loopStack.Push((endLbl, condLbl));
        StartBlock(bodyLbl);
        EmitBoundBlock(dw.Body);
        if (!_blockEnded) EndBlock(new BrTerm(condLbl));
        _loopStack.Pop();

        StartBlock(condLbl);
        var condReg = EmitExpr(dw.Cond);
        EndBlock(new BrCondTerm(condReg, bodyLbl, endLbl));

        StartBlock(endLbl);
    }

    private void EmitBoundFor(BoundFor fs)
    {
        if (fs.Init != null) EmitBoundStmt(fs.Init);

        string condLbl = FreshLabel("cond");
        string bodyLbl = FreshLabel("body");
        string incrLbl = FreshLabel("incr");
        string endLbl  = FreshLabel("end");

        EndBlock(new BrTerm(condLbl));

        StartBlock(condLbl);
        if (fs.Cond != null)
        {
            var condReg = EmitExpr(fs.Cond);
            EndBlock(new BrCondTerm(condReg, bodyLbl, endLbl));
        }
        else
        {
            EndBlock(new BrTerm(bodyLbl));
        }

        _loopStack.Push((endLbl, incrLbl));
        StartBlock(bodyLbl);
        EmitBoundBlock(fs.Body);
        if (!_blockEnded) EndBlock(new BrTerm(incrLbl));
        _loopStack.Pop();

        StartBlock(incrLbl);
        if (fs.Increment != null) EmitExpr(fs.Increment);
        EndBlock(new BrTerm(condLbl));

        StartBlock(endLbl);
    }

    private void EmitBoundForeach(BoundForeach fe)
    {
        var collReg = EmitExpr(fe.Collection);

        // Duck-typed source-class iteration: if collection is a class with
        // `get_Item(int)` + (`Count` field OR `Count()` method), lower via VCall/FieldGet
        // instead of array ops.
        var (_, isClassIter, countIsField) = ClassIterTarget(fe.Collection.Type);

        string indexVar = $"__fe_i_{_nextLabelId}";
        var zeroReg = Alloc(IrType.I32);
        Emit(new ConstI32Instr(zeroReg, 0));
        WriteBackName(indexVar, zeroReg);

        // Compute length once before the loop.
        var lenReg = EmitForeachLength(collReg, isClassIter, countIsField);

        string condLbl = FreshLabel("fe_cond");
        string bodyLbl = FreshLabel("fe_body");
        string incrLbl = FreshLabel("fe_inc");
        string endLbl  = FreshLabel("fe_end");

        EndBlock(new BrTerm(condLbl));

        StartBlock(condLbl);
        var iReg   = _locals[indexVar];
        var cmpReg = Alloc(IrType.Bool);
        Emit(new LtInstr(cmpReg, iReg, lenReg));
        EndBlock(new BrCondTerm(cmpReg, bodyLbl, endLbl));

        _loopStack.Push((endLbl, incrLbl));
        StartBlock(bodyLbl);
        var indexReg = _locals[indexVar];
        var elemReg = Alloc(ToIrType(fe.VarType));
        if (isClassIter)
            Emit(new VCallInstr(elemReg, collReg, "get_Item", new List<TypedReg> { indexReg }));
        else
            Emit(new ArrayGetInstr(elemReg, collReg, indexReg));
        WriteBackName(fe.VarName, elemReg);
        EmitBoundBlock(fe.Body);
        if (!_blockEnded) EndBlock(new BrTerm(incrLbl));
        _loopStack.Pop();

        StartBlock(incrLbl);
        var indexReg2 = _locals[indexVar];
        var oneReg  = Alloc(IrType.I32);
        Emit(new ConstI32Instr(oneReg, 1));
        var nextReg = Alloc(IrType.I32);
        Emit(new AddInstr(nextReg, indexReg2, oneReg));
        WriteBackName(indexVar, nextReg);
        EndBlock(new BrTerm(condLbl));

        StartBlock(endLbl);
    }

    /// Emit the foreach loop's length read — either `coll.Count` field (when
    /// `countIsField`), `coll.Count()` VCall (when class-iter without field),
    /// or `ArrayLen` (built-in array path).
    private TypedReg EmitForeachLength(TypedReg collReg, bool isClassIter, bool countIsField)
    {
        var lenReg = Alloc(IrType.I32);
        if (isClassIter)
        {
            if (countIsField)
                Emit(new FieldGetInstr(lenReg, collReg, "Count"));
            else
                Emit(new VCallInstr(lenReg, collReg, "Count", new List<TypedReg>()));
        }
        else
            Emit(new ArrayLenInstr(lenReg, collReg));
        return lenReg;
    }

    /// Detect a class-typed foreach target with `get_Item` + count source —
    /// returns the class definition and whether Count is a field (true) or method (false).
    /// Pseudo-class `List`/`Dictionary` are excluded so they stick to the builtin
    /// iterator path in interp/jit while L3-G4h step3 migration is in flight.
    private static (Z42ClassType? def, bool isClassIter, bool countIsField)
        ClassIterTarget(Z42Type t)
    {
        Z42ClassType? def = t switch
        {
            Z42InstantiatedType inst => inst.Definition,
            Z42ClassType ct          => ct,
            _                        => null,
        };
        if (def is null) return (null, false, false);
        if (!def.Methods.ContainsKey("get_Item")) return (null, false, false);
        if (def.Fields.ContainsKey("Count"))   return (def, true,  true);
        if (def.Methods.ContainsKey("Count"))  return (def, true,  false);
        return (null, false, false);
    }
}
