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
                // Try arity-qualified name first, then bare name for overloaded methods
                var arityMethod = $"{call.MethodName}${argRegs.Count}";
                if (!_ctx.DepIndex.TryGetStatic(call.ReceiverClass!, arityMethod, out var depEntry))
                    _ctx.DepIndex.TryGetStatic(call.ReceiverClass!, call.MethodName!, out depEntry);
                if (depEntry is not null)
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
            {
                var objReg = EmitExpr(call.Receiver!);

                // Builtin type method: Array
                // 2026-04-26 script-first-stringbuilder: StringBuilder removed from
                // pseudo-class list (now pure z42 script). Only Array remains as
                // pseudo-class (its methods like .Length live on built-in array values).
                if (IsBuiltinCollectionType(call.ReceiverClass))
                {
                    string? builtinName = ResolveBuiltinMethod(call.MethodName!, argRegs.Count);
                    if (builtinName is not null)
                    {
                        var fullArgRegs = new List<TypedReg> { objReg };
                        fullArgRegs.AddRange(argRegs);
                        var dst = Alloc(ToIrType(call.Type));
                        Emit(new BuiltinInstr(dst, builtinName, fullArgRegs));
                        return dst;
                    }
                }

                // For non-builtin-collection types, just try DepIndex

                // Instance methods: try DepIndex next (for stdlib methods not in builtin resolution).
                // L3-G4d: only consult DepIndex when the receiver is an imported class
                // (or unknown). A locally-defined class with the same short name as a stdlib
                // class must dispatch to its OWN method, not be hijacked by a DepIndex match
                // on method-name+arity alone (e.g. user `class Stack` vs `Std.Collections.Stack`).
                bool receiverIsLocalClass = call.ReceiverClass is not null
                    && !_ctx.ImportedClassNamespaces.ContainsKey(call.ReceiverClass);
                if (!receiverIsLocalClass
                    && _ctx.DepIndex.TryGetInstance(call.MethodName!, call.Args.Count, out var depEntry))
                {
                    // 2026-05-04 fix-default-param-cross-cu (D-9)：DepIndex 路径
                    // 此前不调 FillDefaults 导致跨 CU 默认参数漏填。FillDefaults
                    // 用 _funcSignatures fallback 走 type-default 路径。
                    argRegs = FillDefaults(depEntry.QualifiedName, argRegs);
                    var fullArgRegs = new List<TypedReg> { objReg };
                    fullArgRegs.AddRange(argRegs);
                    _ctx.TrackDepNamespace(depEntry.Namespace);
                    var dst = Alloc(ToIrType(call.Type));
                    Emit(new CallInstr(dst, depEntry.QualifiedName, fullArgRegs));
                    return dst;
                }

                // User-defined class instance methods: fall back to virtual dispatch
                var vcallKey = _ctx.FindVcallParamsKey(call.MethodName!, argRegs.Count);
                if (vcallKey is not null)
                {
                    argRegs = FillDefaults(vcallKey, argRegs);
                }
                else if (call.ReceiverClass is not null)
                {
                    // 2026-05-04 fix-default-param-cross-cu (D-9)：FuncParams 没找到
                    // （imported 类）→ 用 ReceiverClass 构造 qualified key 查
                    // _funcSignatures 直接 fallback。受限于具体 receiver 避免
                    // 全 _entries 遍历的歧义匹配（如多类同名方法）。
                    var receiverKey = $"{_ctx.QualifyClassName(call.ReceiverClass)}.{call.MethodName}";
                    if (_ctx.TryGetMethodSignature(receiverKey, out _))
                        argRegs = FillDefaults(receiverKey, argRegs);
                }
                var dst2 = Alloc(ToIrType(call.Type));
                Emit(new VCallInstr(dst2, objReg, call.MethodName!, argRegs));
                return dst2;
            }

            case BoundCallKind.Virtual:
            {
                var objReg = EmitExpr(call.Receiver!);

                // Virtual dispatch for interface/abstract methods
                var vcallKey = _ctx.FindVcallParamsKey(call.MethodName!, argRegs.Count);
                if (vcallKey is not null) argRegs = FillDefaults(vcallKey, argRegs);
                var dst = Alloc(ToIrType(call.Type));
                Emit(new VCallInstr(dst, objReg, call.MethodName!, argRegs));
                return dst;
            }

            case BoundCallKind.Free:
            {
                // impl-lambda-l2: indirect call via FuncRef-typed expression.
                // BindCall populates `Receiver` (with `CalleeName == null`) when the
                // callee is a local var or arbitrary expr of `Z42FuncType`.
                if (call.Receiver is { } indirectCallee)
                {
                    var calleeReg = EmitExpr(indirectCallee);
                    var idst      = Alloc(ToIrType(call.Type));
                    Emit(new CallIndirectInstr(idst, calleeReg, argRegs));
                    return idst;
                }

                string callName;
                // impl-closure-l3-core: a call to a name defined as a *capturing*
                // local fn in the current emitter scope is dispatched indirectly
                // via the closure value stored in `_locals[name]`. Capturing
                // local fns are removed from `_localFnLiftedNames` (handled at
                // the declaration site). See impl-closure-l3-core Decision 9.
                if (call.CalleeName is { } cnCap
                    && !_localFnLiftedNames.ContainsKey(cnCap)
                    && _locals.TryGetValue(cnCap, out var closureReg))
                {
                    var dstClosure = Alloc(ToIrType(call.Type));
                    Emit(new CallIndirectInstr(dstClosure, closureReg, argRegs));
                    return dstClosure;
                }

                // impl-local-fn-l2: a call to a name defined as a local fn in
                // the current emitter scope routes to its lifted module-level
                // name (`<Owner>__<LocalName>`). Local fn shadows top-level.
                if (call.CalleeName is { } cn0
                    && _localFnLiftedNames.TryGetValue(cn0, out var liftedName))
                    callName = liftedName;
                else if (_ctx.TopLevelFunctionNames.Contains(call.CalleeName!))
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

    /// L3-G4h step3 + 2026-04-26 script-first-stringbuilder: pseudo-class List /
    /// Dictionary / StringBuilder removed —— 仅 Array 仍走 BuiltinInstr fast path
    /// (.Length / .Resize 直接作用于 VM 的 Array Value)。
    private bool IsBuiltinCollectionType(string? className)
    {
        return className == "Array";
    }

    /// Map builtin type method names to their BuiltinInstr function name.
    /// 当前无 Array 实例方法走这条路径（Array.Length 通过 builtin 但不经由此处）。
    private string? ResolveBuiltinMethod(string method, int userArgCount)
    {
        return null;
    }

    /// Fill omitted trailing args with their default value expressions.
    ///
    /// 2026-05-04 fix-default-param-cross-cu (D-9)：双层 fallback —
    ///   1. 优先 `_ctx.FuncParams` + `BoundDefaults`（local CU，用户写的真实
    ///      默认表达式）；
    ///   2. 跨 CU（imported 方法 FuncParams 不覆盖）→ 查 `_funcSignatures`
    ///      用 Z42FuncType.RequiredCount + Params 类型 emit type-default const
    ///      （bool=false / int=0 / ref=null 等）。完整用户 default 跨 CU 退化
    ///      为 type-default —— TSIG 当前不导出 default value 表达式，留 follow-up。
    private List<TypedReg> FillDefaults(string qualifiedName, List<TypedReg> argRegs)
    {
        if (_ctx.FuncParams.TryGetValue(qualifiedName, out var parms))
        {
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

        // Cross-CU fallback：用 Z42FuncType + type-default const
        if (_ctx.TryGetMethodSignature(qualifiedName, out var sig))
        {
            int total = sig.Params.Count;
            if (argRegs.Count >= total) return argRegs;
            var filled = new List<TypedReg>(argRegs);
            for (int i = argRegs.Count; i < total; i++)
                filled.Add(EmitTypeDefault(sig.Params[i]));
            return filled;
        }
        return argRegs;
    }

    /// 2026-05-04 fix-default-param-cross-cu (D-9)：emit `Z42Type` 的 type-default
    /// const 到新分配的 register。用于跨 CU 调用 fallback 填充缺位 default param。
    private TypedReg EmitTypeDefault(Z42Type t)
    {
        switch (t)
        {
            case Z42PrimType pt:
                switch (pt.Name)
                {
                    case "bool":
                    {
                        var dst = Alloc(IrType.Bool);
                        Emit(new ConstBoolInstr(dst, false));
                        return dst;
                    }
                    case "int" or "i32" or "short" or "i16" or "byte" or "u8" or "sbyte" or "i8" or "ushort" or "u16" or "uint" or "u32":
                    {
                        var dst = Alloc(IrType.I32);
                        Emit(new ConstI32Instr(dst, 0));
                        return dst;
                    }
                    case "long" or "i64" or "ulong" or "u64":
                    {
                        var dst = Alloc(IrType.I64);
                        Emit(new ConstI64Instr(dst, 0));
                        return dst;
                    }
                    case "float" or "f32" or "double" or "f64":
                    {
                        var dst = Alloc(IrType.F64);
                        Emit(new ConstF64Instr(dst, 0.0));
                        return dst;
                    }
                    case "char":
                    {
                        var dst = Alloc(IrType.Char);
                        Emit(new ConstCharInstr(dst, '\0'));
                        return dst;
                    }
                    case "string":
                    {
                        var dst = Alloc(IrType.Ref);
                        Emit(new ConstNullInstr(dst));
                        return dst;
                    }
                    default:
                    {
                        var dst = Alloc(IrType.Ref);
                        Emit(new ConstNullInstr(dst));
                        return dst;
                    }
                }
            default:
            {
                // 引用类型 / Option / Class / Interface / Func / Array / GenericParam
                var dst = Alloc(IrType.Ref);
                Emit(new ConstNullInstr(dst));
                return dst;
            }
        }
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
