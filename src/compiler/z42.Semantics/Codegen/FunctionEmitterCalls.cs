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

                // Builtin type method: List, Dictionary, Array, StringBuilder
                // Only try builtin methods when ReceiverClass is explicitly a builtin collection type.
                // When ReceiverClass is null, rely on DepIndex to distinguish between
                // different method implementations (e.g., String.Contains vs List.Contains)
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

                // Instance methods: try DepIndex next (for stdlib methods not in builtin resolution)
                if (_ctx.DepIndex.TryGetInstance(call.MethodName!, call.Args.Count, out var depEntry))
                {
                    var fullArgRegs = new List<TypedReg> { objReg };
                    fullArgRegs.AddRange(argRegs);
                    _ctx.TrackDepNamespace(depEntry.Namespace);
                    var dst = Alloc(ToIrType(call.Type));
                    Emit(new CallInstr(dst, depEntry.QualifiedName, fullArgRegs));
                    return dst;
                }

                // User-defined class instance methods: fall back to virtual dispatch
                var vcallKey = _ctx.FindVcallParamsKey(call.MethodName!, argRegs.Count);
                if (vcallKey is not null) argRegs = FillDefaults(vcallKey, argRegs);
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

    /// Check if a class name refers to a builtin collection type.
    private bool IsBuiltinCollectionType(string? className)
    {
        return className is "List" or "Dictionary" or "Array" or "StringBuilder";
    }

    /// Map method names on builtin types to their BuiltinInstr function names.
    /// Only maps methods that are UNIQUE to builtin types and not ambiguous with stdlib.
    /// For ambiguous methods like Contains, this is only called when ReceiverClass is explicitly a builtin type.
    /// Returns null if the method is not a known builtin type method.
    private string? ResolveBuiltinMethod(string method, int userArgCount)
    {
        return method switch
        {
            // List/Array-specific methods
            "Add"           => "__list_add",
            "RemoveAt"      => "__list_remove_at",
            "Insert"        => "__list_insert",
            "Remove"        => "__dict_remove",  // handles both List and Dictionary
            "Clear"         => "__list_clear",
            "Sort"          => "__list_sort",
            "Reverse"       => "__list_reverse",
            "Contains"      => "__list_contains",  // Only used when ReceiverClass is explicitly "List"
            // Dictionary-specific methods
            "ContainsKey"   => "__dict_contains_key",
            "TryGetValue"   => "__dict_try_get_value",
            "Keys"          => "__dict_keys",
            "Values"        => "__dict_values",
            // StringBuilder-specific methods
            "Append"        => "__sb_append",
            "AppendLine"    => "__sb_append_line",
            "AppendNewLine" => "__sb_append_newline",
            _ => null
        };
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
