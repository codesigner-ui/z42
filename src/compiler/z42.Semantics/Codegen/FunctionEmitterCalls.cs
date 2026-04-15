using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Parser;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Call, string interpolation, and switch expression emission — part of FunctionEmitter.
internal sealed partial class FunctionEmitter
{
    // ── Bound call dispatcher ─────────────────────────────────────────────────

    private int EmitBoundCall(BoundCall call)
    {
        var argRegs = call.Args.Select(EmitExpr).ToList();

        switch (call.Kind)
        {
            case BoundCallKind.Static:
            {
                var qualClass = _gen.QualifyName(call.ReceiverClass!);
                var arityKey  = $"{call.MethodName}${argRegs.Count}";
                string resolved = call.MethodName!;
                if (_gen._classStaticMethods.TryGetValue(qualClass, out var sSet))
                    resolved = sSet.Contains(call.MethodName!) ? call.MethodName!
                             : sSet.Contains(arityKey)         ? arityKey : call.MethodName!;
                var callName = $"{qualClass}.{resolved}";
                argRegs = FillDefaults(callName, argRegs);
                int dst = Alloc();
                Emit(new CallInstr(dst, callName, argRegs));
                return dst;
            }

            case BoundCallKind.Instance:
            case BoundCallKind.Virtual:
            {
                int objReg   = EmitExpr(call.Receiver!);
                var vcallKey = _gen.FindVcallParamsKey(call.MethodName!, argRegs.Count);
                if (vcallKey is not null) argRegs = FillDefaults(vcallKey, argRegs);
                int dst = Alloc();
                Emit(new VCallInstr(dst, objReg, call.MethodName!, argRegs));
                return dst;
            }

            case BoundCallKind.Free:
            {
                string callName;
                if (_gen._topLevelFunctionNames.Contains(call.CalleeName!))
                    callName = _gen.QualifyName(call.CalleeName!);
                else if (_currentClassName is not null
                    && _gen._classStaticMethods.TryGetValue(
                        _gen.QualifyName(_currentClassName), out var cSet)
                    && cSet.Contains(call.CalleeName!))
                    callName = $"{_gen.QualifyName(_currentClassName)}.{call.CalleeName!}";
                else
                    callName = call.CalleeName!;
                argRegs = FillDefaults(callName, argRegs);
                int dst = Alloc();
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

    private int EmitUnresolvedCall(BoundCall call, List<int> argRegs)
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
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, builtinName, argRegs));
                return dst;
            }
        }

        // Stdlib static: Console.X, Assert.X, Math.X, etc.
        if (receiverName != null && methodName != null
            && _gen._stdlibIndex.TryGetStatic(receiverName, methodName, out var stdStaticEntry))
        {
            if (receiverName == "Console" && argRegs.Count != 1)
                argRegs = [EmitConcat(argRegs)];
            _gen._usedStdlibNamespaces.Add(stdStaticEntry.Namespace);
            int dst = Alloc();
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
                int receiverReg  = EmitExpr(call.Receiver);
                var collArgRegs  = new List<int> { receiverReg };
                collArgRegs.AddRange(argRegs);
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, collBuiltin, collArgRegs));
                return dst;
            }
        }

        // Stdlib instance: str.Substring, str.ToLower, etc.
        if (methodName != null
            && _gen._stdlibIndex.TryGetInstance(methodName, call.Args.Count, out var stdInstEntry))
        {
            int receiverReg = call.Receiver != null ? EmitExpr(call.Receiver) : Alloc();
            var fullArgRegs = new List<int> { receiverReg };
            fullArgRegs.AddRange(argRegs);
            _gen._usedStdlibNamespaces.Add(stdInstEntry.Namespace);
            int dst = Alloc();
            Emit(new CallInstr(dst, stdInstEntry.QualifiedName, fullArgRegs));
            return dst;
        }

        // Fallback: virtual dispatch if we have a receiver and method name
        if (call.Receiver != null && methodName != null)
        {
            int receiverReg  = EmitExpr(call.Receiver);
            var vcallKey     = _gen.FindVcallParamsKey(methodName, argRegs.Count);
            if (vcallKey is not null) argRegs = FillDefaults(vcallKey, argRegs);
            int dst = Alloc();
            Emit(new VCallInstr(dst, receiverReg, methodName, argRegs));
            return dst;
        }

        throw new NotSupportedException(
            $"unresolved call to `{methodName ?? call.CalleeName ?? "unknown"}`");
    }

    /// Fill omitted trailing args with their default value expressions.
    private List<int> FillDefaults(string qualifiedName, List<int> argRegs)
    {
        if (!_gen._funcParams.TryGetValue(qualifiedName, out var parms)) return argRegs;
        if (argRegs.Count >= parms.Count) return argRegs;
        var filled = new List<int>(argRegs);
        for (int i = argRegs.Count; i < parms.Count; i++)
        {
            var defaultExpr = parms[i].Default
                ?? throw new InvalidOperationException(
                    $"missing argument {i + 1} for `{qualifiedName}` and no default value");
            filled.Add(EmitRawExpr(defaultExpr));
        }
        return filled;
    }

    // ── String interpolation ──────────────────────────────────────────────────

    private int EmitInterpolation(BoundInterpolatedStr interp)
    {
        if (interp.Parts.Count == 0)
        {
            int dst = Alloc();
            Emit(new ConstStrInstr(dst, _gen.Intern("")));
            return dst;
        }
        var partRegs = interp.Parts.Select(EmitBoundPart).ToList();
        return EmitConcat(partRegs);
    }

    private int EmitBoundPart(BoundInterpolationPart part) => part switch
    {
        BoundTextPart tp => EmitBoundTextPart(tp),
        BoundExprPart ep => EmitBoundExprPart(ep),
        _ => throw new NotSupportedException(part.GetType().Name)
    };

    private int EmitBoundTextPart(BoundTextPart tp)
    {
        int dst = Alloc();
        Emit(new ConstStrInstr(dst, _gen.Intern(tp.Text)));
        return dst;
    }

    private int EmitBoundExprPart(BoundExprPart ep)
    {
        int exprReg = EmitExpr(ep.Inner);
        bool isStringLit = ep.Inner is BoundLitStr or BoundInterpolatedStr;
        if (!isStringLit)
        {
            int strReg = Alloc();
            Emit(new ToStrInstr(strReg, exprReg));
            return strReg;
        }
        return exprReg;
    }

    private int EmitConcat(List<int> regs)
    {
        int result = regs[0];
        for (int i = 1; i < regs.Count; i++)
        {
            int dst = Alloc();
            Emit(new StrConcatInstr(dst, result, regs[i]));
            result = dst;
        }
        return result;
    }

    // ── Switch expression ─────────────────────────────────────────────────────

    private int EmitBoundSwitchExpr(BoundSwitchExpr sw)
    {
        int subjReg = EmitExpr(sw.Subject);
        int result  = Alloc();
        string endLbl = FreshLabel("sw_end");

        foreach (var arm in sw.Arms)
        {
            if (arm.Pattern == null)
            {
                int defReg = EmitExpr(arm.Body);
                Emit(new CopyInstr(result, defReg));
                EndBlock(new BrTerm(endLbl));
                break;
            }

            string thenLbl = FreshLabel("sw_arm");
            string nextLbl = FreshLabel("sw_next");

            int patReg  = EmitExpr(arm.Pattern);
            int cmpReg  = Alloc();
            Emit(new EqInstr(cmpReg, subjReg, patReg));
            EndBlock(new BrCondTerm(cmpReg, thenLbl, nextLbl));

            StartBlock(thenLbl);
            int bodyReg = EmitExpr(arm.Body);
            Emit(new CopyInstr(result, bodyReg));
            EndBlock(new BrTerm(endLbl));

            StartBlock(nextLbl);
        }

        if (!_blockEnded) EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }
}
