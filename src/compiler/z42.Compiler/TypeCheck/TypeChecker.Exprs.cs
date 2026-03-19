using Z42.Compiler.Diagnostics;
using Z42.Compiler.Parser;

namespace Z42.Compiler.TypeCheck;

/// Expression type inference — part of the TypeChecker partial class.
public sealed partial class TypeChecker
{
    // ── Pseudo-classes whose calls are resolved at runtime, not type-checked ──
    private static readonly HashSet<string> BuiltinPseudoClasses = ["Console", "Assert", "Math", "List"];

    // ── Expression dispatcher ─────────────────────────────────────────────────

    private Z42Type CheckExpr(Expr expr, TypeEnv env)
    {
        switch (expr)
        {
            // Literals
            case LitIntExpr lit:   return lit.Value is > int.MaxValue or < int.MinValue ? Z42Type.Long : Z42Type.Int;
            case LitFloatExpr:     return Z42Type.Double;
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
                    if (ct.Fields.TryGetValue(m.Member, out var ft))  return ft;
                    if (ct.Methods.TryGetValue(m.Member, out var mt)) return mt;
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"type `{ct.Name}` has no member `{m.Member}`", m.Span);
                    return Z42Type.Error;
                }
                return m.Member switch
                {
                    "Length" when targetType is Z42ArrayType  => Z42Type.Int,
                    "Length" when targetType == Z42Type.String => Z42Type.Int,
                    _ => Z42Type.Unknown
                };
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

            case NullCoalesceExpr nc:
            {
                var leftType  = CheckExpr(nc.Left,  env);
                var rightType = CheckExpr(nc.Right, env);
                return Z42Type.IsAssignableTo(leftType, rightType) ? leftType
                     : Z42Type.IsAssignableTo(rightType, leftType) ? rightType
                     : Z42Type.Unknown;
            }

            case CastExpr cast:
                CheckExpr(cast.Operand, env);
                return ResolveType(cast.TargetType);

            case NewExpr newExpr:
                foreach (var arg in newExpr.Args) CheckExpr(arg, env);
                return ResolveType(newExpr.Type);

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
        _diags.UndefinedSymbol(id.Name, id.Span);
        return Z42Type.Error;
    }

    // ── Assignment ────────────────────────────────────────────────────────────

    private Z42Type CheckAssign(AssignExpr assign, TypeEnv env)
    {
        var targetType = CheckExpr(assign.Target, env);
        var valueType  = CheckExpr(assign.Value,  env);
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

        switch (bin.Op)
        {
            case "+" when lt == Z42Type.String || rt == Z42Type.String:
                return Z42Type.String;

            case "+" or "-" or "*" or "/" or "%":
                if (!Z42Type.IsNumeric(lt) && lt is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{bin.Op}` requires numeric operand, got `{lt}`", bin.Left.Span);
                else if (!Z42Type.IsNumeric(rt) && rt is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{bin.Op}` requires numeric operand, got `{rt}`", bin.Right.Span);
                return (lt is Z42ErrorType || rt is Z42ErrorType)
                    ? Z42Type.Error
                    : Z42Type.ArithmeticResult(lt, rt);

            case "<" or "<=" or ">" or ">=":
                if (!Z42Type.IsNumeric(lt) && lt is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{bin.Op}` requires numeric operand, got `{lt}`", bin.Left.Span);
                return Z42Type.Bool;

            case "==" or "!=":
                return Z42Type.Bool;

            case "&&" or "||":
                if (!Z42Type.IsBool(lt) && lt is not Z42UnknownType and not Z42ErrorType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{bin.Op}` requires bool operand, got `{lt}`", bin.Left.Span);
                if (!Z42Type.IsBool(rt) && rt is not Z42UnknownType and not Z42ErrorType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{bin.Op}` requires bool operand, got `{rt}`", bin.Right.Span);
                return Z42Type.Bool;

            case "is" or "as":
                return Z42Type.Bool;

            default:
                return Z42Type.Unknown;
        }
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
            case "await":
                return Z42Type.Unknown;
            default:
                return t;
        }
    }

    // ── Call ──────────────────────────────────────────────────────────────────

    private Z42Type CheckCall(CallExpr call, TypeEnv env)
    {
        // Pseudo-class static calls (Console.*, Assert.*, Math.*, List.*) — skip symbol resolution
        if (call.Callee is MemberExpr { Target: IdentExpr pseudoId }
            && BuiltinPseudoClasses.Contains(pseudoId.Name))
        {
            foreach (var a in call.Args) CheckExpr(a, env);
            return Z42Type.Void;
        }

        // Member method calls (instance methods or user-defined class methods)
        if (call.Callee is MemberExpr mCallee)
        {
            var receiverType = CheckExpr(mCallee.Target, env);
            var argTypes     = call.Args.Select(a => CheckExpr(a, env)).ToList();

            if (receiverType is Z42ClassType ct)
            {
                if (ct.Methods.TryGetValue(mCallee.Member, out var mt))
                {
                    if (argTypes.Count != mt.Params.Count)
                        _diags.Error(DiagnosticCodes.TypeMismatch,
                            $"expected {mt.Params.Count} argument(s), got {argTypes.Count}", call.Span);
                    return mt.Ret;
                }
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type `{ct.Name}` has no method `{mCallee.Member}`", call.Span);
                return Z42Type.Error;
            }

            // String built-in methods
            if (receiverType == Z42Type.String)
                return mCallee.Member switch
                {
                    "Substring"                                => Z42Type.String,
                    "Contains" or "StartsWith" or "EndsWith"  => Z42Type.Bool,
                    _                                          => Z42Type.Unknown
                };

            return Z42Type.Unknown; // generic method call on unknown type
        }

        // Free function call
        var callArgTypes = call.Args.Select(a => CheckExpr(a, env)).ToList();

        Z42Type calleeType = call.Callee is IdentExpr funcId
            ? (env.LookupFunc(funcId.Name) as Z42Type ?? CheckIdent(funcId, env))
            : CheckExpr(call.Callee, env);

        if (calleeType is Z42FuncType funcType)
        {
            if (callArgTypes.Count != funcType.Params.Count)
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"expected {funcType.Params.Count} argument(s), got {callArgTypes.Count}", call.Span);
            else
                for (int i = 0; i < callArgTypes.Count; i++)
                    RequireAssignable(funcType.Params[i], callArgTypes[i], call.Args[i].Span,
                        $"argument {i + 1}: expected `{funcType.Params[i]}`, got `{callArgTypes[i]}`");
            return funcType.Ret;
        }

        return Z42Type.Unknown;
    }
}
