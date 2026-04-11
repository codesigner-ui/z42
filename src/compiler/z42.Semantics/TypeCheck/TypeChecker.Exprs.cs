using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// Expression type inference — part of the TypeChecker partial class.
public sealed partial class TypeChecker
{
    // ── Expression dispatcher ─────────────────────────────────────────────────

    private Z42Type CheckExpr(Expr expr, TypeEnv env)
    {
        switch (expr)
        {
            // Literals
            case LitIntExpr lit:   return lit.Value is > int.MaxValue or < int.MinValue ? Z42Type.Long : Z42Type.Int;
            case LitFloatExpr f:   return f.IsFloat ? Z42Type.Float : Z42Type.Double;
            case LitStrExpr:       return Z42Type.String;
            case LitBoolExpr:      return Z42Type.Bool;
            case LitNullExpr:      return Z42Type.Null;
            case LitCharExpr:      return Z42Type.Char;

            case InterpolatedStrExpr interp:
                foreach (var p in interp.Parts)
                    if (p is ExprPart ep) CheckExpr(ep.Inner, env);
                return Z42Type.String;

            case IdentExpr id:         return CheckIdent(id, env);
            case AssignExpr assign:    return CheckAssign(assign, env);
            case BinaryExpr bin:       return CheckBinary(bin, env);
            case UnaryExpr u:          return CheckUnary(u, env);

            case PostfixExpr post:
            {
                var t = CheckExpr(post.Operand, env);
                if (post.Op is "++" or "--" && !Z42Type.IsNumeric(t) && t is not Z42ErrorType and not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{post.Op}` requires numeric operand, got `{t}`", post.Span);
                return t;
            }

            case CallExpr call:        return CheckCall(call, env);

            case MemberExpr m:
            {
                var targetType = CheckExpr(m.Target, env);
                if (targetType is Z42ClassType ct)
                {
                    bool insideClass = _currentClass == ct.Name;
                    if (ct.Fields.TryGetValue(m.Member, out var ft))
                    {
                        if (!insideClass
                            && ct.MemberVisibility.TryGetValue(m.Member, out var fv)
                            && fv == Visibility.Private)
                            _diags.Error(DiagnosticCodes.AccessViolation,
                                $"field `{m.Member}` is private to `{ct.Name}`", m.Span);
                        return ft;
                    }
                    if (ct.Methods.TryGetValue(m.Member, out var mt))
                    {
                        if (!insideClass
                            && ct.MemberVisibility.TryGetValue(m.Member, out var mv)
                            && mv == Visibility.Private)
                            _diags.Error(DiagnosticCodes.AccessViolation,
                                $"method `{m.Member}` is private to `{ct.Name}`", m.Span);
                        return mt;
                    }
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"type `{ct.Name}` has no member `{m.Member}`", m.Span);
                    return Z42Type.Error;
                }
                // Interface member access
                if (targetType is Z42InterfaceType ifaceType
                    && ifaceType.Methods.TryGetValue(m.Member, out var ifmt))
                    return ifmt;

                // Well-known members on built-in types
                if (m.Member == "Length" && (targetType is Z42ArrayType || targetType == Z42Type.String))
                    return Z42Type.Int;
                if (m.Member == "Count")
                    return Z42Type.Int;

                return Z42Type.Unknown;
            }

            case IndexExpr ix:
                CheckExpr(ix.Index, env);
                return ElemTypeOf(CheckExpr(ix.Target, env));

            case ConditionalExpr ternary:
            {
                RequireBool(CheckExpr(ternary.Cond, env), ternary.Cond.Span, "ternary");
                var thenType = CheckExpr(ternary.Then, env);
                var elseType = CheckExpr(ternary.Else, env);
                if (thenType is Z42ErrorType || elseType is Z42ErrorType) return Z42Type.Error;
                return Z42Type.IsAssignableTo(thenType, elseType) ? thenType
                     : Z42Type.IsAssignableTo(elseType, thenType) ? elseType
                     : Z42Type.Unknown;
            }

            case NullConditionalExpr nc:
            {
                var targetType = CheckExpr(nc.Target, env);
                if (targetType is Z42ClassType ct)
                {
                    if (ct.Fields.TryGetValue(nc.Member, out var ft))  return ft;
                    if (ct.Methods.TryGetValue(nc.Member, out var mt)) return mt.Ret;
                }
                return nc.Member is "Length" or "Count" ? Z42Type.Int : Z42Type.Unknown;
            }

            case NullCoalesceExpr nc:
            {
                var leftType  = CheckExpr(nc.Left,  env);
                var rightType = CheckExpr(nc.Right, env);
                // T? ?? T  →  T  (unwrap optional)
                var innerLeft = leftType is Z42OptionType opt ? opt.Inner : leftType;
                return Z42Type.IsAssignableTo(innerLeft, rightType) ? innerLeft
                     : Z42Type.IsAssignableTo(rightType, innerLeft) ? rightType
                     : Z42Type.Unknown;
            }

            case IsPatternExpr ipe:
            {
                CheckExpr(ipe.Target, env);
                // Define the binding variable as the named type in the current env.
                // (For proper branch-scoping the caller — IfStmt — may push a subscope.)
                var bindType = _classes.TryGetValue(ipe.TypeName, out var ct)
                    ? (Z42Type)ct
                    : Z42Type.Unknown;
                env.Define(ipe.Binding, bindType);
                return Z42Type.Bool;
            }

            case CastExpr cast:
                CheckExpr(cast.Operand, env);
                return ResolveType(cast.TargetType);

            case NewExpr newExpr:
            {
                foreach (var arg in newExpr.Args) CheckExpr(arg, env);
                var newType = ResolveType(newExpr.Type);
                if (newExpr.Type is NamedType { Name: var newName }
                    && _abstractClasses.Contains(newName))
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"cannot instantiate abstract class `{newName}`", newExpr.Span);
                return newType;
            }

            case ArrayCreateExpr ac:
            {
                var sizeType = CheckExpr(ac.Size, env);
                if (!Z42Type.IsNumeric(sizeType) && sizeType is not Z42ErrorType and not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"array size must be numeric, got `{sizeType}`", ac.Size.Span);
                return new Z42ArrayType(ResolveType(ac.ElemType));
            }

            case ArrayLitExpr al:
            {
                var elemType = ResolveType(al.ElemType);
                foreach (var e in al.Elements) RequireAssignable(elemType, CheckExpr(e, env), e.Span);
                return new Z42ArrayType(elemType);
            }

            case SwitchExpr sw:
            {
                CheckExpr(sw.Subject, env);
                Z42Type resultType = Z42Type.Unknown;
                foreach (var arm in sw.Arms)
                {
                    if (arm.Pattern != null) CheckExpr(arm.Pattern, env);
                    resultType = CheckExpr(arm.Body, env);
                }
                return resultType;
            }

            default:
                return Z42Type.Unknown;
        }
    }

    // ── Identifier ────────────────────────────────────────────────────────────

    private Z42Type CheckIdent(IdentExpr id, TypeEnv env)
    {
        var t = env.LookupVar(id.Name) ?? env.LookupFunc(id.Name);
        if (t != null) return t;
        if (_enumTypes.Contains(id.Name)) return Z42Type.Unknown; // enum type name
        if (_classes.ContainsKey(id.Name)) return Z42Type.Unknown; // class name used as static target
        if (_interfaces.ContainsKey(id.Name)) return Z42Type.Unknown; // interface name
        _diags.UndefinedSymbol(id.Name, id.Span);
        return Z42Type.Error;
    }

    // ── Assignment ────────────────────────────────────────────────────────────

    private Z42Type CheckAssign(AssignExpr assign, TypeEnv env)
    {
        var targetType = CheckExpr(assign.Target, env);
        var valueType  = CheckExpr(assign.Value,  env);
        var intLitVal = ExtractIntLiteralValue(assign.Value);
        if (intLitVal != null)
        {
            var rangeOk = TryCheckIntLiteralRange(targetType, intLitVal.Value, assign.Value.Span);
            if (rangeOk == null)
                RequireAssignable(targetType, valueType, assign.Value.Span);
        }
        else
            RequireAssignable(targetType, valueType, assign.Value.Span);
        // Narrow from Unknown after first assignment
        if (assign.Target is IdentExpr id && targetType is Z42UnknownType)
            env.Define(id.Name, valueType);
        return valueType;
    }

    // ── Binary ────────────────────────────────────────────────────────────────

    private Z42Type CheckBinary(BinaryExpr bin, TypeEnv env)
    {
        var lt = CheckExpr(bin.Left,  env);
        var rt = CheckExpr(bin.Right, env);
        if (lt is Z42ErrorType || rt is Z42ErrorType) return Z42Type.Error;

        // "+" string concatenation: if either operand is string, no numeric constraint
        if (bin.Op == "+" && (lt == Z42Type.String || rt == Z42Type.String))
            return Z42Type.String;

        if (!BinaryTypeTable.Rules.TryGetValue(bin.Op, out var rule))
            return Z42Type.Unknown;

        CheckBinaryOperand(rule.LeftOk,  rule.Requirement, lt, bin.Left.Span,  bin.Op);
        CheckBinaryOperand(rule.RightOk, rule.Requirement, rt, bin.Right.Span, bin.Op);

        return (lt is Z42ErrorType || rt is Z42ErrorType)
            ? Z42Type.Error
            : rule.Output(lt, rt);
    }

    /// Validates one operand against a BinaryTypeRule constraint.
    /// Does nothing if constraint is null or the type is an error/unknown sentinel.
    private void CheckBinaryOperand(
        Func<Z42Type, bool>? constraint,
        string               requirement,
        Z42Type              t,
        Span                 span,
        string               op)
    {
        if (constraint == null || t is Z42UnknownType or Z42ErrorType) return;
        if (!constraint(t))
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"operator `{op}` requires {requirement} operand, got `{t}`", span);
    }

    // ── Unary ─────────────────────────────────────────────────────────────────

    private Z42Type CheckUnary(UnaryExpr u, TypeEnv env)
    {
        var t = CheckExpr(u.Operand, env);
        if (t is Z42ErrorType) return Z42Type.Error;
        switch (u.Op)
        {
            case "!":
                if (!Z42Type.IsBool(t) && t is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `!` requires bool, got `{t}`", u.Operand.Span);
                return Z42Type.Bool;
            case "-" or "+":
                if (!Z42Type.IsNumeric(t) && t is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"unary `{u.Op}` requires numeric operand, got `{t}`", u.Operand.Span);
                return t;
            case "++" or "--":
                if (!Z42Type.IsNumeric(t) && t is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{u.Op}` requires numeric operand, got `{t}`", u.Operand.Span);
                return t;
            case "~":
                if (!Z42Type.IsIntegral(t) && t is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `~` requires integral operand, got `{t}`", u.Operand.Span);
                return t;
            case "await":
                return Z42Type.Unknown;
            default:
                return t;
        }
    }

    // ── Call ──────────────────────────────────────────────────────────────────

    private Z42Type CheckCall(CallExpr call, TypeEnv env)
    {
        // ── User-defined static class method: ClassName.StaticMethod(args) ────
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var clsName }, Member: var staticMember }
            && _classes.TryGetValue(clsName, out var staticCt))
        {
            // Try exact name, then arity-qualified name for overloaded methods.
            var arityKey = $"{staticMember}${call.Args.Count}";
            var staticSig = staticCt.StaticMethods.TryGetValue(staticMember, out var s1) ? s1
                          : staticCt.StaticMethods.TryGetValue(arityKey,     out var s2) ? s2
                          : null;
            if (staticSig is not null)
            {
                var argTs = call.Args.Select(a => CheckExpr(a, env)).ToList();
                if (argTs.Count < staticSig.MinArgCount || argTs.Count > staticSig.Params.Count)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"expected {staticSig.MinArgCount}–{staticSig.Params.Count} argument(s), got {argTs.Count}", call.Span);
                else
                    for (int i = 0; i < argTs.Count; i++)
                        RequireAssignable(staticSig.Params[i], argTs[i], call.Args[i].Span,
                            $"argument {i + 1}: expected `{staticSig.Params[i]}`, got `{argTs[i]}`");
                return staticSig.Ret;
            }
        }

        // ── Member method calls: instance methods and builtin instance methods ─
        if (call.Callee is MemberExpr mCallee)
        {
            // If the target is a bare identifier not known to the user type system, treat it as
            // a potential stdlib/external class (e.g. Console, Assert, Math).  Don't report
            // UndefinedSymbol — resolution happens at IrGen time via StdlibCallIndex.
            if (mCallee.Target is IdentExpr { Name: var tgtName }
                && !_classes.ContainsKey(tgtName)
                && !_interfaces.ContainsKey(tgtName)
                && !_enumTypes.Contains(tgtName)
                && env.LookupVar(tgtName) == null
                && env.LookupFunc(tgtName) == null)
            {
                foreach (var a in call.Args) CheckExpr(a, env);
                return Z42Type.Unknown;
            }

            var receiverType = CheckExpr(mCallee.Target, env);
            var argTypes     = call.Args.Select(a => CheckExpr(a, env)).ToList();

            // User-defined class instance method
            if (receiverType is Z42ClassType ct)
            {
                // Try exact name, then arity-qualified name for overloaded methods.
                var instArityKey = $"{mCallee.Member}${argTypes.Count}";
                var mt = ct.Methods.TryGetValue(mCallee.Member, out var mt1) ? mt1
                       : ct.Methods.TryGetValue(instArityKey,   out var mt2) ? mt2
                       : null;
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
                    if (argTypes.Count < mt.MinArgCount || argTypes.Count > mt.Params.Count)
                        _diags.Error(DiagnosticCodes.TypeMismatch,
                            $"expected {mt.MinArgCount}–{mt.Params.Count} argument(s), got {argTypes.Count}", call.Span);
                    else
                        for (int i = 0; i < argTypes.Count; i++)
                            RequireAssignable(mt.Params[i], argTypes[i], call.Args[i].Span,
                                $"argument {i + 1}: expected `{mt.Params[i]}`, got `{argTypes[i]}`");
                    return mt.Ret;
                }
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type `{ct.Name}` has no method `{mCallee.Member}`", call.Span);
                return Z42Type.Error;
            }

            // Interface method call
            if (receiverType is Z42InterfaceType ifaceType)
            {
                if (ifaceType.Methods.TryGetValue(mCallee.Member, out var imt))
                {
                    if (argTypes.Count < imt.MinArgCount || argTypes.Count > imt.Params.Count)
                        _diags.Error(DiagnosticCodes.TypeMismatch,
                            $"expected {imt.MinArgCount}–{imt.Params.Count} argument(s), got {argTypes.Count}", call.Span);
                    else
                        for (int i = 0; i < argTypes.Count; i++)
                            RequireAssignable(imt.Params[i], argTypes[i], call.Args[i].Span,
                                $"argument {i + 1}: expected `{imt.Params[i]}`, got `{argTypes[i]}`");
                    return imt.Ret;
                }
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"interface `{ifaceType.Name}` has no method `{mCallee.Member}`", call.Span);
                return Z42Type.Error;
            }

            return Z42Type.Unknown; // method on unknown/primitive type — stdlib resolves at IrGen time
        }

        // ── Free function call ────────────────────────────────────────────────
        var callArgTypes = call.Args.Select(a => CheckExpr(a, env)).ToList();

        // Bare name inside same class → check current class's static methods first.
        if (call.Callee is IdentExpr { Name: var bareCallName }
            && _currentClass != null
            && _classes.TryGetValue(_currentClass, out var curCt)
            && curCt.StaticMethods.TryGetValue(bareCallName, out var bareSig))
        {
            if (callArgTypes.Count < bareSig.MinArgCount || callArgTypes.Count > bareSig.Params.Count)
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"expected {bareSig.MinArgCount}–{bareSig.Params.Count} argument(s), got {callArgTypes.Count}", call.Span);
            else
                for (int i = 0; i < callArgTypes.Count; i++)
                    RequireAssignable(bareSig.Params[i], callArgTypes[i], call.Args[i].Span,
                        $"argument {i + 1}: expected `{bareSig.Params[i]}`, got `{callArgTypes[i]}`");
            return bareSig.Ret;
        }

        Z42Type calleeType = call.Callee is IdentExpr funcId
            ? (env.LookupFunc(funcId.Name) as Z42Type ?? CheckIdent(funcId, env))
            : CheckExpr(call.Callee, env);

        if (calleeType is Z42FuncType funcType)
        {
            if (callArgTypes.Count < funcType.MinArgCount || callArgTypes.Count > funcType.Params.Count)
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"expected {funcType.MinArgCount}–{funcType.Params.Count} argument(s), got {callArgTypes.Count}", call.Span);
            else
                for (int i = 0; i < callArgTypes.Count; i++)
                    RequireAssignable(funcType.Params[i], callArgTypes[i], call.Args[i].Span,
                        $"argument {i + 1}: expected `{funcType.Params[i]}`, got `{callArgTypes[i]}`");
            return funcType.Ret;
        }

        return Z42Type.Unknown;
    }
}
