using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Parser;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Call, string interpolation, and switch expression emission — part of FunctionEmitter.
internal sealed partial class FunctionEmitter
{
    // ── Bound call dispatcher ─────────────────────────────────────────────────

    private TypedReg EmitBoundCall(BoundCall call)
    {
        var argRegs = call.Args.Select(EmitExpr).ToList();

        switch (call.Kind)
        {
            case BoundCallKind.Static:
            {
                // Try DepIndex first (for stdlib methods)
                if (_ctx.DepIndex.TryGetStatic(call.ReceiverClass!, call.MethodName!, out var depEntry))
                {
                    if (call.ReceiverClass == "Console" && argRegs.Count != 1)
                        argRegs = [EmitConcat(argRegs)];
                    _ctx.TrackDepNamespace(depEntry.Namespace);
                    var dst = Alloc(ToIrType(call.Type));
                    Emit(new CallInstr(dst, depEntry.QualifiedName, argRegs));
                    return dst;
                }

                // Fall back to user-defined static methods
                var qualClass = _ctx.QualifyClassName(call.ReceiverClass!);
                // Track dependency namespace for imported class calls
                if (_ctx.ImportedClassNamespaces.TryGetValue(call.ReceiverClass!, out var depNs))
                    _ctx.TrackDepNamespace(depNs);
                var arityKey  = $"{call.MethodName}${argRegs.Count}";
                string resolved = call.MethodName!;
                if (_ctx.ClassRegistry.TryGetStaticMethods(qualClass, out var sSet))
                    resolved = sSet.Contains(call.MethodName!) ? call.MethodName!
                             : sSet.Contains(arityKey)         ? arityKey : call.MethodName!;
                var callName = $"{qualClass}.{resolved}";
                argRegs = FillDefaults(callName, argRegs);
                var dst2 = Alloc(ToIrType(call.Type));
                Emit(new CallInstr(dst2, callName, argRegs));
                return dst2;
            }

            case BoundCallKind.Instance:
            case BoundCallKind.Virtual:
            {
                var objReg = EmitExpr(call.Receiver!);

                // Try DepIndex for stdlib instance methods
                if (call.Kind == BoundCallKind.Instance
                    && _ctx.DepIndex.TryGetInstance(call.MethodName!, call.Args.Count, out var depEntry))
                {
                    var fullArgRegs = new List<TypedReg> { objReg };
                    fullArgRegs.AddRange(argRegs);
                    _ctx.TrackDepNamespace(depEntry.Namespace);
                    var dst = Alloc(ToIrType(call.Type));
                    Emit(new CallInstr(dst, depEntry.QualifiedName, fullArgRegs));
                    return dst;
                }

                // Fall back to virtual dispatch
                var vcallKey = _ctx.FindVcallParamsKey(call.MethodName!, argRegs.Count);
                if (vcallKey is not null) argRegs = FillDefaults(vcallKey, argRegs);
                var dst2 = Alloc(ToIrType(call.Type));
                Emit(new VCallInstr(dst2, objReg, call.MethodName!, argRegs));
                return dst2;
            }

            case BoundCallKind.Free:
            {
                string callName;
                if (_ctx.TopLevelFunctionNames.Contains(call.CalleeName!))
                    callName = _ctx.QualifyName(call.CalleeName!);
                else if (_currentClassName is not null
                    && _ctx.ClassRegistry.TryGetStaticMethods(
                        _ctx.QualifyName(_currentClassName), out var cSet)
                    && cSet.Contains(call.CalleeName!))
                    callName = $"{_ctx.QualifyName(_currentClassName)}.{call.CalleeName!}";
                else
                    callName = call.CalleeName!;
                argRegs = FillDefaults(callName, argRegs);
                var dst = Alloc(ToIrType(call.Type));
                Emit(new CallInstr(dst, callName, argRegs));
                return dst;
            }

            default:
                throw new NotSupportedException($"call kind {call.Kind}");
        }
    }

    /// Fill omitted trailing args with their default value expressions.
    private List<TypedReg> FillDefaults(string qualifiedName, List<TypedReg> argRegs)
    {
        if (!_ctx.FuncParams.TryGetValue(qualifiedName, out var parms)) return argRegs;
        if (argRegs.Count >= parms.Count) return argRegs;
        var filled   = new List<TypedReg>(argRegs);
        var defaults = _ctx.SemanticModel.BoundDefaults;
        for (int i = argRegs.Count; i < parms.Count; i++)
        {
            if (!defaults.TryGetValue(parms[i], out var boundDefault))
                throw new InvalidOperationException(
                    $"missing argument {i + 1} for `{qualifiedName}` and no bound default");
            filled.Add(EmitExpr(boundDefault));
        }
        return filled;
    }

    // ── String interpolation ──────────────────────────────────────────────────

    private TypedReg EmitInterpolation(BoundInterpolatedStr interp)
    {
        if (interp.Parts.Count == 0)
        {
            var dst = Alloc(IrType.Str);
            Emit(new ConstStrInstr(dst, _ctx.Intern("")));
            return dst;
        }
        var partRegs = interp.Parts.Select(EmitBoundPart).ToList();
        return EmitConcat(partRegs);
    }

    private TypedReg EmitBoundPart(BoundInterpolationPart part) => part switch
    {
        BoundTextPart tp => EmitBoundTextPart(tp),
        BoundExprPart ep => EmitBoundExprPart(ep),
        _ => throw new NotSupportedException(part.GetType().Name)
    };

    private TypedReg EmitBoundTextPart(BoundTextPart tp)
    {
        var dst = Alloc(IrType.Str);
        Emit(new ConstStrInstr(dst, _ctx.Intern(tp.Text)));
        return dst;
    }

    private TypedReg EmitBoundExprPart(BoundExprPart ep)
    {
        var exprReg = EmitExpr(ep.Inner);
        bool isStringLit = ep.Inner is BoundLitStr or BoundInterpolatedStr;
        if (!isStringLit)
        {
            var strReg = Alloc(IrType.Str);
            Emit(new ToStrInstr(strReg, exprReg));
            return strReg;
        }
        return exprReg;
    }

    private TypedReg EmitConcat(List<TypedReg> regs)
    {
        var result = regs[0];
        for (int i = 1; i < regs.Count; i++)
        {
            var dst = Alloc(IrType.Str);
            Emit(new StrConcatInstr(dst, result, regs[i]));
            result = dst;
        }
        return result;
    }

    // ── Switch expression ─────────────────────────────────────────────────────

    private TypedReg EmitBoundSwitchExpr(BoundSwitchExpr sw)
    {
        var subjReg = EmitExpr(sw.Subject);
        var result  = Alloc(ToIrType(sw.Type));
        string endLbl = FreshLabel("sw_end");

        foreach (var arm in sw.Arms)
        {
            if (arm.Pattern == null)
            {
                var defReg = EmitExpr(arm.Body);
                Emit(new CopyInstr(result, defReg));
                EndBlock(new BrTerm(endLbl));
                break;
            }

            string thenLbl = FreshLabel("sw_arm");
            string nextLbl = FreshLabel("sw_next");

            var patReg  = EmitExpr(arm.Pattern);
            var cmpReg  = Alloc(IrType.Bool);
            Emit(new EqInstr(cmpReg, subjReg, patReg));
            EndBlock(new BrCondTerm(cmpReg, thenLbl, nextLbl));

            StartBlock(thenLbl);
            var bodyReg = EmitExpr(arm.Body);
            Emit(new CopyInstr(result, bodyReg));
            EndBlock(new BrTerm(endLbl));

            StartBlock(nextLbl);
        }

        if (!_blockEnded) EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }
}
