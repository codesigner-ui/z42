using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// Expression binding — part of the TypeChecker partial class.
public sealed partial class TypeChecker
{
    // ── Expression binder ─────────────────────────────────────────────────────

    /// Binds <paramref name="expr"/> to a typed <see cref="BoundExpr"/>.
    /// <paramref name="expectedType"/> guides integer literal sizing when the
    /// target type is known (e.g. in variable declarations and assignments).
    private BoundExpr BindExpr(Expr expr, TypeEnv env, Z42Type? expectedType = null)
    {
        switch (expr)
        {
            case LitIntExpr lit:
            {
                var litType = expectedType != null && Z42Type.IntLiteralRange(expectedType) != null
                    ? expectedType
                    : lit.Value is > int.MaxValue or < int.MinValue ? Z42Type.Long : Z42Type.Int;
                return new BoundLitInt(lit.Value, litType, lit.Span);
            }

            case LitFloatExpr f:    return new BoundLitFloat(f.Value, f.IsFloat, f.Span);
            case LitStrExpr s:      return new BoundLitStr(s.Value, s.Span);
            case LitBoolExpr b:     return new BoundLitBool(b.Value, b.Span);
            case LitNullExpr n:     return new BoundLitNull(n.Span);
            case LitCharExpr c:     return new BoundLitChar(c.Value, c.Span);

            case InterpolatedStrExpr interp:
            {
                var parts = new List<BoundInterpolationPart>(interp.Parts.Count);
                foreach (var p in interp.Parts)
                    parts.Add(p is ExprPart ep
                        ? new BoundExprPart(BindExpr(ep.Inner, env), ep.Span)
                        : new BoundTextPart(((TextPart)p).Text, p.Span));
                return new BoundInterpolatedStr(parts, interp.Span);
            }

            case IdentExpr id:      return BindIdent(id, env);
            case AssignExpr assign: return BindAssign(assign, env);
            case BinaryExpr bin:    return BindBinary(bin, env);
            case UnaryExpr u:       return BindUnary(u, env);

            case PostfixExpr post:
            {
                var operand = BindExpr(post.Operand, env);
                if (post.Op is "++" or "--"
                    && !Z42Type.IsNumeric(operand.Type)
                    && operand.Type is not Z42ErrorType and not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{post.Op}` requires numeric operand, got `{operand.Type}`", post.Span);
                return new BoundPostfix(post.Op, operand, operand.Type, post.Span);
            }

            case CallExpr call:     return BindCall(call, env);
            case MemberExpr m:      return BindMemberExpr(m, env);

            case IndexExpr ix:
            {
                var target = BindExpr(ix.Target, env);
                var index  = BindExpr(ix.Index,  env);
                return new BoundIndex(target, index, ElemTypeOf(target.Type), ix.Span);
            }

            case ConditionalExpr ternary:
            {
                var cond = BindExpr(ternary.Cond, env);
                RequireBool(cond.Type, ternary.Cond.Span, "ternary");
                var then = BindExpr(ternary.Then, env);
                var els  = BindExpr(ternary.Else, env);
                if (then.Type is Z42ErrorType || els.Type is Z42ErrorType)
                    return new BoundConditional(cond, then, els, Z42Type.Error, ternary.Span);
                var condType = Z42Type.IsAssignableTo(then.Type, els.Type) ? then.Type
                             : Z42Type.IsAssignableTo(els.Type, then.Type) ? els.Type
                             : Z42Type.Unknown;
                return new BoundConditional(cond, then, els, condType, ternary.Span);
            }

            case NullConditionalExpr nc:
            {
                var target = BindExpr(nc.Target, env);
                Z42Type memberType = nc.Member is "Length" or "Count" ? Z42Type.Int : Z42Type.Unknown;
                if (target.Type is Z42ClassType nct)
                {
                    if (nct.Fields.TryGetValue(nc.Member, out var ft))        memberType = ft;
                    else if (nct.Methods.TryGetValue(nc.Member, out var mt2)) memberType = mt2.Ret;
                }
                return new BoundNullConditional(target, nc.Member, memberType, nc.Span);
            }

            case NullCoalesceExpr nc:
            {
                var left  = BindExpr(nc.Left,  env);
                var right = BindExpr(nc.Right, env);
                var innerLeft = left.Type is Z42OptionType opt ? opt.Inner : left.Type;
                var coalesce  = Z42Type.IsAssignableTo(innerLeft, right.Type) ? innerLeft
                              : Z42Type.IsAssignableTo(right.Type, innerLeft) ? right.Type
                              : Z42Type.Unknown;
                return new BoundNullCoalesce(left, right, coalesce, nc.Span);
            }

            case IsPatternExpr ipe:
            {
                var target   = BindExpr(ipe.Target, env);
                var bindType = _classes.TryGetValue(ipe.TypeName, out var ct)
                    ? (Z42Type)ct : Z42Type.Unknown;
                env.Define(ipe.Binding, bindType);
                return new BoundIsPattern(target, ipe.TypeName, ipe.Binding, bindType, ipe.Span);
            }

            case CastExpr cast:
            {
                var operand  = BindExpr(cast.Operand, env);
                var castType = ResolveType(cast.TargetType);
                return new BoundCast(operand, castType, cast.Span);
            }

            case NewExpr newExpr:
            {
                var args    = newExpr.Args.Select(a => BindExpr(a, env)).ToList();
                var newType = ResolveType(newExpr.Type);
                if (newExpr.Type is NamedType { Name: var newName }
                    && _abstractClasses.Contains(newName))
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"cannot instantiate abstract class `{newName}`", newExpr.Span);
                var qualName = newExpr.Type is NamedType nt ? nt.Name : newType.ToString()!;
                return new BoundNew(qualName, args, newType, newExpr.Span);
            }

            case ArrayCreateExpr ac:
            {
                var size = BindExpr(ac.Size, env);
                if (!Z42Type.IsNumeric(size.Type) && size.Type is not Z42ErrorType and not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"array size must be numeric, got `{size.Type}`", ac.Size.Span);
                return new BoundArrayCreate(size, ResolveType(ac.ElemType), ac.Span);
            }

            case ArrayLitExpr al:
            {
                var elemType = ResolveType(al.ElemType);
                var elems    = al.Elements.Select(e =>
                {
                    var be = BindExpr(e, env);
                    RequireAssignable(elemType, be.Type, e.Span);
                    return be;
                }).ToList();
                return new BoundArrayLit(elems, elemType, al.Span);
            }

            case SwitchExpr sw:
            {
                var subject    = BindExpr(sw.Subject, env);
                Z42Type resultType = Z42Type.Unknown;
                var arms       = new List<BoundSwitchArm>(sw.Arms.Count);
                foreach (var arm in sw.Arms)
                {
                    var pattern = arm.Pattern != null ? BindExpr(arm.Pattern, env) : null;
                    var body    = BindExpr(arm.Body, env);
                    resultType  = body.Type;
                    arms.Add(new BoundSwitchArm(pattern, body, arm.Span));
                }
                return new BoundSwitchExpr(subject, arms, resultType, sw.Span);
            }

            case ErrorExpr e:
                return new BoundError("error expr", Z42Type.Error, e.Span);

            default:
                return new BoundError("unknown expr", Z42Type.Unknown, expr.Span);
        }
    }

    // ── Identifier ────────────────────────────────────────────────────────────

    private BoundExpr BindIdent(IdentExpr id, TypeEnv env)
    {
        var t = env.LookupVar(id.Name) ?? env.LookupFunc(id.Name);
        if (t != null) return new BoundIdent(id.Name, t, id.Span);
        if (_enumTypes.Contains(id.Name))    return new BoundIdent(id.Name, Z42Type.Unknown, id.Span);
        if (_classes.ContainsKey(id.Name))   return new BoundIdent(id.Name, Z42Type.Unknown, id.Span);
        if (_interfaces.ContainsKey(id.Name)) return new BoundIdent(id.Name, Z42Type.Unknown, id.Span);
        _diags.UndefinedSymbol(id.Name, id.Span);
        return new BoundError($"undefined `{id.Name}`", Z42Type.Error, id.Span);
    }

    // ── Assignment ────────────────────────────────────────────────────────────

    private BoundExpr BindAssign(AssignExpr assign, TypeEnv env)
    {
        var target    = BindExpr(assign.Target, env);
        var intLitVal = ExtractIntLiteralValue(assign.Value);
        BoundExpr value;
        if (intLitVal != null)
        {
            var rangeOk = TryCheckIntLiteralRange(target.Type, intLitVal.Value, assign.Value.Span);
            value = BindExpr(assign.Value, env, target.Type);
            if (rangeOk == null)
                RequireAssignable(target.Type, value.Type, assign.Value.Span);
        }
        else
        {
            value = BindExpr(assign.Value, env);
            RequireAssignable(target.Type, value.Type, assign.Value.Span);
        }
        // Narrow from Unknown after first assignment
        if (assign.Target is IdentExpr id && target.Type is Z42UnknownType)
            env.Define(id.Name, value.Type);
        return new BoundAssign(target, value, value.Type, assign.Span);
    }

    // ── Binary ────────────────────────────────────────────────────────────────

    private BoundExpr BindBinary(BinaryExpr bin, TypeEnv env)
    {
        var left  = BindExpr(bin.Left,  env);
        var right = BindExpr(bin.Right, env);
        if (left.Type is Z42ErrorType || right.Type is Z42ErrorType)
            return new BoundBinary(bin.Op, left, right, Z42Type.Error, bin.Span);

        if (bin.Op == "+" && (left.Type == Z42Type.String || right.Type == Z42Type.String))
            return new BoundBinary(bin.Op, left, right, Z42Type.String, bin.Span);

        if (!BinaryTypeTable.Rules.TryGetValue(bin.Op, out var rule))
            return new BoundBinary(bin.Op, left, right, Z42Type.Unknown, bin.Span);

        CheckBinaryOperand(rule.LeftOk,  rule.Requirement, left.Type,  bin.Left.Span,  bin.Op);
        CheckBinaryOperand(rule.RightOk, rule.Requirement, right.Type, bin.Right.Span, bin.Op);

        var outType = (left.Type is Z42ErrorType || right.Type is Z42ErrorType)
            ? Z42Type.Error : rule.Output(left.Type, right.Type);
        return new BoundBinary(bin.Op, left, right, outType, bin.Span);
    }

    private void CheckBinaryOperand(
        Func<Z42Type, bool>? constraint, string requirement,
        Z42Type t, Span span, string op)
    {
        if (constraint == null || t is Z42UnknownType or Z42ErrorType) return;
        if (!constraint(t))
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"operator `{op}` requires {requirement} operand, got `{t}`", span);
    }

    // ── Unary ─────────────────────────────────────────────────────────────────

    private BoundExpr BindUnary(UnaryExpr u, TypeEnv env)
    {
        var operand = BindExpr(u.Operand, env);
        if (operand.Type is Z42ErrorType)
            return new BoundUnary(u.Op, operand, Z42Type.Error, u.Span);
        Z42Type outType;
        switch (u.Op)
        {
            case "!":
                if (!Z42Type.IsBool(operand.Type) && operand.Type is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `!` requires bool, got `{operand.Type}`", u.Operand.Span);
                outType = Z42Type.Bool; break;
            case "-" or "+":
                if (!Z42Type.IsNumeric(operand.Type) && operand.Type is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"unary `{u.Op}` requires numeric operand, got `{operand.Type}`", u.Operand.Span);
                outType = operand.Type; break;
            case "++" or "--":
                if (!Z42Type.IsNumeric(operand.Type) && operand.Type is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{u.Op}` requires numeric operand, got `{operand.Type}`", u.Operand.Span);
                outType = operand.Type; break;
            case "~":
                if (!Z42Type.IsIntegral(operand.Type) && operand.Type is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `~` requires integral operand, got `{operand.Type}`", u.Operand.Span);
                outType = operand.Type; break;
            default:
                outType = u.Op == "await" ? Z42Type.Unknown : operand.Type; break;
        }
        return new BoundUnary(u.Op, operand, outType, u.Span);
    }

    // ── Member expression (non-call) ──────────────────────────────────────────

    private BoundExpr BindMemberExpr(MemberExpr m, TypeEnv env)
    {
        var target = BindExpr(m.Target, env);
        if (target.Type is Z42ClassType ct)
        {
            bool insideClass = _currentClass == ct.Name;
            if (ct.Fields.TryGetValue(m.Member, out var ft))
            {
                if (!insideClass
                    && ct.MemberVisibility.TryGetValue(m.Member, out var fv)
                    && fv == Visibility.Private)
                    _diags.Error(DiagnosticCodes.AccessViolation,
                        $"field `{m.Member}` is private to `{ct.Name}`", m.Span);
                return new BoundMember(target, m.Member, ft, m.Span);
            }
            if (ct.Methods.TryGetValue(m.Member, out var mt))
            {
                if (!insideClass
                    && ct.MemberVisibility.TryGetValue(m.Member, out var mv)
                    && mv == Visibility.Private)
                    _diags.Error(DiagnosticCodes.AccessViolation,
                        $"method `{m.Member}` is private to `{ct.Name}`", m.Span);
                return new BoundMember(target, m.Member, mt, m.Span);
            }
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"type `{ct.Name}` has no member `{m.Member}`", m.Span);
            return new BoundError($"no member `{m.Member}`", Z42Type.Error, m.Span);
        }
        if (target.Type is Z42InterfaceType ifaceType
            && ifaceType.Methods.TryGetValue(m.Member, out var ifmt))
            return new BoundMember(target, m.Member, ifmt, m.Span);
        if (m.Member is "Length" && (target.Type is Z42ArrayType || target.Type == Z42Type.String))
            return new BoundMember(target, m.Member, Z42Type.Int, m.Span);
        if (m.Member == "Count")
            return new BoundMember(target, m.Member, Z42Type.Int, m.Span);
        return new BoundMember(target, m.Member, Z42Type.Unknown, m.Span);
    }

    // ── Call ──────────────────────────────────────────────────────────────────

    private BoundCall BindCall(CallExpr call, TypeEnv env)
    {
        // ── User-defined static class method: ClassName.Method(args) ──────────
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var clsName }, Member: var staticMember }
            && _classes.TryGetValue(clsName, out var staticCt))
        {
            var arityKey  = $"{staticMember}${call.Args.Count}";
            var staticSig = staticCt.StaticMethods.TryGetValue(staticMember, out var s1) ? s1
                          : staticCt.StaticMethods.TryGetValue(arityKey,     out var s2) ? s2 : null;
            if (staticSig is not null)
            {
                var args = BindAndCheckArgs(call.Args, env, staticSig.Params,
                    staticSig.MinArgCount, call.Span);
                return new BoundCall(BoundCallKind.Static, null, clsName, staticMember,
                    null, args, staticSig.Ret, call.Span);
            }
        }

        // ── Member call ───────────────────────────────────────────────────────
        if (call.Callee is MemberExpr mCallee)
        {
            // Unknown target: stdlib/external class (e.g. Console, Assert, Math)
            if (mCallee.Target is IdentExpr { Name: var tgtName }
                && !_classes.ContainsKey(tgtName) && !_interfaces.ContainsKey(tgtName)
                && !_enumTypes.Contains(tgtName)
                && env.LookupVar(tgtName) == null && env.LookupFunc(tgtName) == null)
            {
                var recv = new BoundIdent(tgtName, Z42Type.Unknown, mCallee.Target.Span);
                var args = call.Args.Select(a => BindExpr(a, env)).ToList();
                return new BoundCall(BoundCallKind.Unresolved, recv, null, mCallee.Member,
                    null, args, Z42Type.Unknown, call.Span);
            }

            var recvExpr = BindExpr(mCallee.Target, env);
            var argBound = call.Args.Select(a => BindExpr(a, env)).ToList();

            if (recvExpr.Type is Z42ClassType ct)
            {
                var instArityKey = $"{mCallee.Member}${argBound.Count}";
                var mt = ct.Methods.TryGetValue(mCallee.Member, out var mt1) ? mt1
                       : ct.Methods.TryGetValue(instArityKey,   out var mt2) ? mt2 : null;
                if (mt is not null)
                {
                    bool insideClass = _currentClass == ct.Name;
                    var visKey = ct.MemberVisibility.ContainsKey(mCallee.Member)
                        ? mCallee.Member : instArityKey;
                    if (!insideClass
                        && ct.MemberVisibility.TryGetValue(visKey, out var mv)
                        && mv == Visibility.Private)
                        _diags.Error(DiagnosticCodes.AccessViolation,
                            $"method `{mCallee.Member}` is private to `{ct.Name}`", call.Span);
                    CheckArgCount(argBound.Count, mt.MinArgCount, mt.Params.Count, call.Span);
                    CheckArgTypes(call.Args, argBound, mt.Params);
                    bool isVirtual = _virtualMethods.TryGetValue(ct.Name, out var vmSet)
                        && (vmSet.Contains(mCallee.Member) || vmSet.Contains(instArityKey));
                    return new BoundCall(
                        isVirtual ? BoundCallKind.Virtual : BoundCallKind.Instance,
                        recvExpr, ct.Name, mCallee.Member, null, argBound, mt.Ret, call.Span);
                }
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type `{ct.Name}` has no method `{mCallee.Member}`", call.Span);
                return new BoundCall(BoundCallKind.Unresolved, recvExpr, null, mCallee.Member,
                    null, argBound, Z42Type.Error, call.Span);
            }

            if (recvExpr.Type is Z42InterfaceType ifaceType)
            {
                if (ifaceType.Methods.TryGetValue(mCallee.Member, out var imt))
                {
                    CheckArgCount(argBound.Count, imt.MinArgCount, imt.Params.Count, call.Span);
                    CheckArgTypes(call.Args, argBound, imt.Params);
                    return new BoundCall(BoundCallKind.Virtual, recvExpr, ifaceType.Name,
                        mCallee.Member, null, argBound, imt.Ret, call.Span);
                }
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"interface `{ifaceType.Name}` has no method `{mCallee.Member}`", call.Span);
                return new BoundCall(BoundCallKind.Unresolved, recvExpr, null, mCallee.Member,
                    null, argBound, Z42Type.Error, call.Span);
            }

            // Unknown/primitive type — stdlib resolves at IrGen time
            return new BoundCall(BoundCallKind.Unresolved, recvExpr, null, mCallee.Member,
                null, argBound, Z42Type.Unknown, call.Span);
        }

        // ── Free function call ────────────────────────────────────────────────
        var freeArgs = call.Args.Select(a => BindExpr(a, env)).ToList();

        // Bare name inside current class's static methods
        if (call.Callee is IdentExpr { Name: var bareCallName }
            && _currentClass != null
            && _classes.TryGetValue(_currentClass, out var curCt)
            && curCt.StaticMethods.TryGetValue(bareCallName, out var bareSig))
        {
            CheckArgCount(freeArgs.Count, bareSig.MinArgCount, bareSig.Params.Count, call.Span);
            CheckArgTypes(call.Args, freeArgs, bareSig.Params);
            return new BoundCall(BoundCallKind.Static, null, _currentClass, bareCallName,
                null, freeArgs, bareSig.Ret, call.Span);
        }

        if (call.Callee is IdentExpr funcId)
        {
            var funcType = env.LookupFunc(funcId.Name);
            if (funcType is Z42FuncType ft)
            {
                CheckArgCount(freeArgs.Count, ft.MinArgCount, ft.Params.Count, call.Span);
                CheckArgTypes(call.Args, freeArgs, ft.Params);
                return new BoundCall(BoundCallKind.Free, null, null, null,
                    funcId.Name, freeArgs, ft.Ret, call.Span);
            }
            // Unknown function — report and return Unresolved
            BindIdent(funcId, env);
            return new BoundCall(BoundCallKind.Unresolved, null, null, null,
                funcId.Name, freeArgs, Z42Type.Unknown, call.Span);
        }

        // Non-identifier callee (rare)
        var calleeExpr = BindExpr(call.Callee, env);
        if (calleeExpr.Type is Z42FuncType ft2)
        {
            CheckArgCount(freeArgs.Count, ft2.MinArgCount, ft2.Params.Count, call.Span);
            CheckArgTypes(call.Args, freeArgs, ft2.Params);
        }
        return new BoundCall(BoundCallKind.Unresolved, calleeExpr, null, null,
            null, freeArgs, calleeExpr.Type is Z42FuncType ft3 ? ft3.Ret : Z42Type.Unknown, call.Span);
    }

    // ── Call helpers ──────────────────────────────────────────────────────────

    private List<BoundExpr> BindAndCheckArgs(
        IReadOnlyList<Expr> args, TypeEnv env,
        IReadOnlyList<Z42Type> paramTypes, int minArgCount, Span callSpan)
    {
        var bound = args.Select(a => BindExpr(a, env)).ToList();
        CheckArgCount(bound.Count, minArgCount, paramTypes.Count, callSpan);
        CheckArgTypes(args, bound, paramTypes);
        return bound;
    }

    private void CheckArgCount(int actual, int min, int max, Span span)
    {
        if (actual < min || actual > max)
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"expected {min}–{max} argument(s), got {actual}", span);
    }

    private void CheckArgTypes(
        IReadOnlyList<Expr>      origArgs,
        IReadOnlyList<BoundExpr> boundArgs,
        IReadOnlyList<Z42Type>   paramTypes)
    {
        int n = Math.Min(boundArgs.Count, paramTypes.Count);
        for (int i = 0; i < n; i++)
            RequireAssignable(paramTypes[i], boundArgs[i].Type, origArgs[i].Span,
                $"argument {i + 1}: expected `{paramTypes[i]}`, got `{boundArgs[i].Type}`");
    }
}
