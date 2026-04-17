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
                var qualClass = _ctx.QualifyName(call.ReceiverClass!);
                var arityKey  = $"{call.MethodName}${argRegs.Count}";
                string resolved = call.MethodName!;
                if (_ctx.ClassRegistry.TryGetStaticMethods(qualClass, out var sSet))
                    resolved = sSet.Contains(call.MethodName!) ? call.MethodName!
                             : sSet.Contains(arityKey)         ? arityKey : call.MethodName!;
                var callName = $"{qualClass}.{resolved}";
                argRegs = FillDefaults(callName, argRegs);
                var dst = Alloc(ToIrType(call.Type));
                Emit(new CallInstr(dst, callName, argRegs));
                return dst;
            }

            case BoundCallKind.Instance:
            case BoundCallKind.Virtual:
            {
                var objReg   = EmitExpr(call.Receiver!);
                var vcallKey = _ctx.FindVcallParamsKey(call.MethodName!, argRegs.Count);
                if (vcallKey is not null) argRegs = FillDefaults(vcallKey, argRegs);
                var dst = Alloc(ToIrType(call.Type));
                Emit(new VCallInstr(dst, objReg, call.MethodName!, argRegs));
                return dst;
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

            case BoundCallKind.Unresolved:
                return EmitUnresolvedCall(call, argRegs);

            default:
                throw new NotSupportedException($"call kind {call.Kind}");
        }
    }

    // ── Unresolved call — stdlib / builtin dispatch ───────────────────────────

    private TypedReg EmitUnresolvedCall(BoundCall call, List<TypedReg> argRegs)
    {
        var receiverName = (call.Receiver as BoundIdent)?.Name;
        var methodName   = call.MethodName;

        // Type-keyword static: string.X, int.X, double.X, etc.
        if (receiverName is "string" or "int" or "long" or "double" or "float" or "bool" or "char")
        {
            string? builtinName = (receiverName, methodName) switch
            {
                ("string", "IsNullOrEmpty")      => "__str_is_null_or_empty",
                ("string", "IsNullOrWhiteSpace") => "__str_is_null_or_whitespace",
                ("string", "Join")               => "__str_join",
                ("string", "Concat")             => "__str_concat",
                ("string", "Format")             => "__str_format",
                ("int",    "Parse")              => "__int_parse",
                ("long",   "Parse")              => "__long_parse",
                ("double", "Parse")              => "__double_parse",
                ("float",  "Parse")              => "__double_parse",
                _ => null
            };
            if (builtinName != null)
            {
                var dst = Alloc(ToIrType(call.Type));
                Emit(new BuiltinInstr(dst, builtinName, argRegs));
                return dst;
            }
        }

        // Stdlib static: Console.X, Assert.X, Math.X, etc.
        if (receiverName != null && methodName != null
            && _ctx.StdlibIndex.TryGetStatic(receiverName, methodName, out var stdStaticEntry))
        {
            if (receiverName == "Console" && argRegs.Count != 1)
                argRegs = [EmitConcat(argRegs)];
            _ctx.TrackStdlibNamespace(stdStaticEntry.Namespace);
            var dst = Alloc(ToIrType(call.Type));
            Emit(new CallInstr(dst, stdStaticEntry.QualifiedName, argRegs));
            return dst;
        }

        // Collection/pseudo-class instance methods: Add, RemoveAt, etc.
        // Kind=Unresolved means TypeChecker confirmed the receiver is NOT a user class.
        if (methodName != null)
        {
            string? collBuiltin = methodName switch
            {
                "Add"         => "__list_add",
                "RemoveAt"    => "__list_remove_at",
                "Insert"      => "__list_insert",
                "Clear"       => "__list_clear",
                "Sort"        => "__list_sort",
                "Reverse"     => "__list_reverse",
                "ContainsKey" => "__dict_contains_key",
                "Remove"      => "__dict_remove",
                "Contains"    => "__contains",
                _ => null
            };
            bool receiverIsClassInst = call.Receiver?.Type is Z42ClassType;
            if (collBuiltin != null && !receiverIsClassInst && call.Receiver != null)
            {
                var receiverReg  = EmitExpr(call.Receiver);
                var collArgRegs  = new List<TypedReg> { receiverReg };
                collArgRegs.AddRange(argRegs);
                var dst = Alloc(ToIrType(call.Type));
                Emit(new BuiltinInstr(dst, collBuiltin, collArgRegs));
                return dst;
            }
        }

        // Stdlib instance: str.Substring, str.ToLower, etc.
        if (methodName != null
            && _ctx.StdlibIndex.TryGetInstance(methodName, call.Args.Count, out var stdInstEntry))
        {
            var receiverReg = call.Receiver != null ? EmitExpr(call.Receiver) : Alloc(IrType.Unknown);
            var fullArgRegs = new List<TypedReg> { receiverReg };
            fullArgRegs.AddRange(argRegs);
            _ctx.TrackStdlibNamespace(stdInstEntry.Namespace);
            var dst = Alloc(ToIrType(call.Type));
            Emit(new CallInstr(dst, stdInstEntry.QualifiedName, fullArgRegs));
            return dst;
        }

        // Fallback: virtual dispatch if we have a receiver and method name
        if (call.Receiver != null && methodName != null)
        {
            var receiverReg  = EmitExpr(call.Receiver);
            var vcallKey     = _ctx.FindVcallParamsKey(methodName, argRegs.Count);
            if (vcallKey is not null) argRegs = FillDefaults(vcallKey, argRegs);
            var dst = Alloc(ToIrType(call.Type));
            Emit(new VCallInstr(dst, receiverReg, methodName, argRegs));
            return dst;
        }

        throw new NotSupportedException(
            $"unresolved call to `{methodName ?? call.CalleeName ?? "unknown"}`");
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
