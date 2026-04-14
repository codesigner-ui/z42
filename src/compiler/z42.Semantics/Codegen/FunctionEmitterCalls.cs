using Z42.Syntax.Parser;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Call, string interpolation, and switch expression emission — part of FunctionEmitter.
internal sealed partial class FunctionEmitter
{
    // ── Call ─────────────────────────────────────────────────────────────────

    private int EmitCall(CallExpr call)
    {
        // ── Static user-defined class method call: ClassName.StaticMethod(args) ──
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var staticCls }, Member: var staticMethod }
            && _gen._classStaticMethods.TryGetValue(_gen.QualifyName(staticCls), out var staticSet))
        {
            var arityKey = $"{staticMethod}${call.Args.Count}";
            var resolvedMethod = staticSet.Contains(staticMethod) ? staticMethod
                               : staticSet.Contains(arityKey)     ? arityKey
                               : null;
            if (resolvedMethod is not null)
            {
                var callName = $"{_gen.QualifyName(staticCls)}.{resolvedMethod}";
                var argRegs  = FillDefaults(callName, call.Args.Select(EmitExpr).ToList());
                int dst = Alloc();
                Emit(new CallInstr(dst, callName, argRegs));
                return dst;
            }
        }

        // ── Type-keyword static calls: string.X, int.X, double.X ────────────
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var kwCls }, Member: var kwMethod }
            && kwCls is "string" or "int" or "long" or "double" or "float" or "bool" or "char")
        {
            var kwArgRegs = call.Args.Select(EmitExpr).ToList();
            string? builtinName = (kwCls, kwMethod) switch {
                ("string", "IsNullOrEmpty")       => "__str_is_null_or_empty",
                ("string", "IsNullOrWhiteSpace")  => "__str_is_null_or_whitespace",
                ("string", "Join")                => "__str_join",
                ("string", "Concat")              => "__str_concat",
                ("string", "Format")              => "__str_format",
                ("int",    "Parse")               => "__int_parse",
                ("long",   "Parse")               => "__long_parse",
                ("double", "Parse")               => "__double_parse",
                ("float",  "Parse")               => "__double_parse",
                _ => null
            };
            if (builtinName != null)
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, builtinName, kwArgRegs));
                return dst;
            }
        }

        // ── Stdlib static calls: Console.X, Assert.X, Math.X ─────────────────
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var stdCls }, Member: var stdMethod }
            && _gen._stdlibIndex.TryGetStatic(stdCls, stdMethod, out var stdStaticEntry))
        {
            var argRegs = call.Args.Select(EmitExpr).ToList();
            if (stdCls == "Console" && argRegs.Count != 1)
                argRegs = [EmitConcat(argRegs)];
            _gen._usedStdlibNamespaces.Add(stdStaticEntry.Namespace);
            int dst = Alloc();
            Emit(new CallInstr(dst, stdStaticEntry.QualifiedName, argRegs));
            return dst;
        }

        // ── List / Dictionary pseudo-class instance methods ──────────────────
        if (call.Callee is MemberExpr { Target: var collTarget, Member: var collMethod })
        {
            string? collBuiltin = collMethod switch {
                "Add"         => "__list_add",
                "RemoveAt"    => "__list_remove_at",
                "Insert"      => "__list_insert",
                "Clear"       => "__list_clear",
                "Sort"        => "__list_sort",
                "Reverse"     => "__list_reverse",
                "ContainsKey" => "__dict_contains_key",
                "Remove"      => "__dict_remove",
                "Contains"    => "__contains",
                _             => null
            };
            bool receiverIsClassInstance = IsReceiverClassInstance(collTarget, collMethod);
            if (collBuiltin != null && !receiverIsClassInstance)
            {
                int receiverReg = EmitExpr(collTarget);
                var collArgRegs = new List<int> { receiverReg };
                collArgRegs.AddRange(call.Args.Select(EmitExpr));
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, collBuiltin, collArgRegs));
                return dst;
            }
        }

        // ── Stdlib instance calls: str.Substring, str.ToLower, etc. ─────────
        if (call.Callee is MemberExpr { Target: var instTarget, Member: var instMethod }
            && _gen._stdlibIndex.TryGetInstance(instMethod, call.Args.Count, out var stdInstEntry))
        {
            int receiverReg = EmitExpr(instTarget);
            var argRegs     = new List<int> { receiverReg };
            argRegs.AddRange(call.Args.Select(EmitExpr));
            _gen._usedStdlibNamespaces.Add(stdInstEntry.Namespace);
            int dst = Alloc();
            Emit(new CallInstr(dst, stdInstEntry.QualifiedName, argRegs));
            return dst;
        }

        // ── Instance method call via virtual dispatch ────────────────────────
        if (call.Callee is MemberExpr mMethod)
        {
            int objReg  = EmitExpr(mMethod.Target);
            var argRegs = call.Args.Select(EmitExpr).ToList();
            var vcallKey = _gen.FindVcallParamsKey(mMethod.Member, argRegs.Count);
            if (vcallKey is not null)
                argRegs = FillDefaults(vcallKey, argRegs);
            int dst = Alloc();
            Emit(new VCallInstr(dst, objReg, mMethod.Member, argRegs));
            return dst;
        }

        // ── Free function call: Foo(args) ─────────────────────────────────────
        if (call.Callee is IdentExpr funcId)
        {
            string callName;
            if (_gen._topLevelFunctionNames.Contains(funcId.Name))
                callName = _gen.QualifyName(funcId.Name);
            else if (_currentClassName is not null
                && _gen._classStaticMethods.TryGetValue(
                    _gen.QualifyName(_currentClassName), out var curStaticSet)
                && curStaticSet.Contains(funcId.Name))
                callName = $"{_gen.QualifyName(_currentClassName)}.{funcId.Name}";
            else
                callName = funcId.Name;
            var argRegs = FillDefaults(callName, call.Args.Select(EmitExpr).ToList());
            int dst = Alloc();
            Emit(new CallInstr(dst, callName, argRegs));
            return dst;
        }

        throw new NotSupportedException($"call pattern not supported: {call.Callee.GetType().Name}");
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
            filled.Add(EmitExpr(defaultExpr));
        }
        return filled;
    }

    // ── String interpolation ──────────────────────────────────────────────────

    private int EmitInterpolation(InterpolatedStrExpr interp)
    {
        if (interp.Parts.Count == 0)
        {
            int dst = Alloc();
            Emit(new ConstStrInstr(dst, _gen.Intern("")));
            return dst;
        }
        var partRegs = interp.Parts.Select(EmitPart).ToList();
        return EmitConcat(partRegs);
    }

    private int EmitPart(InterpolationPart part) => part switch
    {
        TextPart tp => EmitTextPart(tp),
        ExprPart ep => EmitExprPart(ep),
        _           => throw new NotSupportedException(part.GetType().Name)
    };

    private int EmitTextPart(TextPart tp)
    {
        int dst = Alloc();
        Emit(new ConstStrInstr(dst, _gen.Intern(tp.Text)));
        return dst;
    }

    private int EmitExprPart(ExprPart ep)
    {
        int exprReg = EmitExpr(ep.Inner);
        bool isStringLit = ep.Inner is LitStrExpr or InterpolatedStrExpr;
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

    private int EmitSwitchExpr(SwitchExpr sw)
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
