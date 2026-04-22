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
                if (UnaryTypeTable.Rules.TryGetValue(post.Op, out var postRule)
                    && postRule.Constraint != null
                    && operand.Type is not Z42ErrorType and not Z42UnknownType
                    && !postRule.Constraint(operand.Type))
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{post.Op}` requires {postRule.Requirement} operand, got `{operand.Type}`",
                        post.Span);
                return new BoundPostfix(ToPostfixOp(post.Op), operand, operand.Type, post.Span);
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
                var bindType = _symbols.Classes.TryGetValue(ipe.TypeName, out var ct)
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
                var qualName = newExpr.Type switch
                {
                    NamedType nt   => nt.Name,
                    GenericType gt => gt.Name,
                    _              => newType.ToString()!,
                };
                if (_symbols.AbstractClasses.Contains(qualName))
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"cannot instantiate abstract class `{qualName}`", newExpr.Span);
                // Validate explicit type argument count matches class type parameter count
                if (newExpr.Type is GenericType gt2
                    && _symbols.Classes.TryGetValue(gt2.Name, out var clsType)
                    && clsType.TypeParams != null)
                {
                    if (gt2.TypeArgs.Count != clsType.TypeParams.Count)
                        _diags.Error(DiagnosticCodes.TypeMismatch,
                            $"generic class `{gt2.Name}` expects {clsType.TypeParams.Count} type argument(s), " +
                            $"but got {gt2.TypeArgs.Count}", newExpr.Span);
                    else
                    {
                        // L3-G2: validate each explicit type arg satisfies the class's where constraints.
                        var resolved = gt2.TypeArgs.Select(ResolveType).ToList();
                        ValidateGenericConstraints(gt2.Name, clsType.TypeParams, resolved,
                            _classConstraints, newExpr.Span);
                    }
                }
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
        // Type references (class, enum, interface, imported class) are not variables;
        // return BoundIdent with Unknown type so downstream member/call resolution handles them.
        if (_symbols.EnumTypes.Contains(id.Name)
            || _symbols.Classes.ContainsKey(id.Name)
            || _symbols.Interfaces.ContainsKey(id.Name)
            || _symbols.ImportedClassNames.Contains(id.Name))
            return new BoundIdent(id.Name, Z42Type.Unknown, id.Span);
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
        var op    = ToBinaryOp(bin.Op);
        if (left.Type is Z42ErrorType || right.Type is Z42ErrorType)
            return new BoundBinary(op, left, right, Z42Type.Error, bin.Span);

        if (bin.Op == "+" && (left.Type == Z42Type.String || right.Type == Z42Type.String))
            return new BoundBinary(op, left, right, Z42Type.String, bin.Span);

        if (!BinaryTypeTable.Rules.TryGetValue(bin.Op, out var rule))
            return new BoundBinary(op, left, right, Z42Type.Unknown, bin.Span);

        CheckBinaryOperand(rule.LeftOk,  rule.Requirement, left.Type,  bin.Left.Span,  bin.Op);
        CheckBinaryOperand(rule.RightOk, rule.Requirement, right.Type, bin.Right.Span, bin.Op);

        var outType = (left.Type is Z42ErrorType || right.Type is Z42ErrorType)
            ? Z42Type.Error : rule.Output(left.Type, right.Type);
        return new BoundBinary(op, left, right, outType, bin.Span);
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
        var op      = ToUnaryOp(u.Op);
        if (operand.Type is Z42ErrorType)
            return new BoundUnary(op, operand, Z42Type.Error, u.Span);

        if (!UnaryTypeTable.Rules.TryGetValue(u.Op, out var rule))
            return new BoundUnary(op, operand, operand.Type, u.Span);

        if (rule.Constraint != null
            && operand.Type is not Z42UnknownType
            && !rule.Constraint(operand.Type))
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"operator `{u.Op}` requires {rule.Requirement} operand, got `{operand.Type}`",
                u.Operand.Span);

        return new BoundUnary(op, operand, rule.Output(operand.Type), u.Span);
    }

    // ── Op converters (string AST → typed Bound enum) ─────────────────────────

    private static BinaryOp ToBinaryOp(string op) => op switch
    {
        "+"  => BinaryOp.Add,  "-"  => BinaryOp.Sub,  "*"  => BinaryOp.Mul,
        "/"  => BinaryOp.Div,  "%"  => BinaryOp.Rem,
        "==" => BinaryOp.Eq,   "!=" => BinaryOp.Ne,
        "<"  => BinaryOp.Lt,   "<=" => BinaryOp.Le,
        ">"  => BinaryOp.Gt,   ">=" => BinaryOp.Ge,
        "&&" => BinaryOp.And,  "||" => BinaryOp.Or,
        "&"  => BinaryOp.BitAnd, "|" => BinaryOp.BitOr, "^" => BinaryOp.BitXor,
        "<<" => BinaryOp.Shl,  ">>" => BinaryOp.Shr,
        "is" => BinaryOp.Is,   "as" => BinaryOp.As,
        _ => throw new InvalidOperationException($"unknown binary op `{op}`")
    };

    private static UnaryOp ToUnaryOp(string op) => op switch
    {
        "-"    => UnaryOp.Neg,
        "+"    => UnaryOp.Plus,
        "!"    => UnaryOp.Not,
        "~"    => UnaryOp.BitNot,
        "++"   => UnaryOp.PrefixInc,
        "--"   => UnaryOp.PrefixDec,
        "await" => UnaryOp.Await,
        _ => throw new InvalidOperationException($"unknown unary op `{op}`")
    };

    private static PostfixOp ToPostfixOp(string op) => op switch
    {
        "++" => PostfixOp.Inc,
        "--" => PostfixOp.Dec,
        _ => throw new InvalidOperationException($"unknown postfix op `{op}`")
    };

    // ── Member expression (non-call) ──────────────────────────────────────────

    private BoundExpr BindMemberExpr(MemberExpr m, TypeEnv env)
    {
        var target = BindExpr(m.Target, env);
        // L3-G4a: substitute Z42InstantiatedType fields/methods with concrete TypeArgs.
        if (target.Type is Z42InstantiatedType inst)
        {
            var subMap = BuildSubstitutionMap(inst);
            var def    = inst.Definition;
            bool insideClass = env.CurrentClass == def.Name;
            if (def.Fields.TryGetValue(m.Member, out var ft))
            {
                if (!insideClass
                    && def.MemberVisibility.TryGetValue(m.Member, out var fv)
                    && fv == Visibility.Private)
                    _diags.Error(DiagnosticCodes.AccessViolation,
                        $"field `{m.Member}` is private to `{def.Name}`", m.Span);
                return new BoundMember(target, m.Member, SubstituteTypeParams(ft, subMap), m.Span);
            }
            if (def.Methods.TryGetValue(m.Member, out var mt))
            {
                if (!insideClass
                    && def.MemberVisibility.TryGetValue(m.Member, out var mv)
                    && mv == Visibility.Private)
                    _diags.Error(DiagnosticCodes.AccessViolation,
                        $"method `{m.Member}` is private to `{def.Name}`", m.Span);
                return new BoundMember(target, m.Member, (Z42FuncType)SubstituteTypeParams(mt, subMap), m.Span);
            }
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"type `{inst}` has no member `{m.Member}`", m.Span);
            return new BoundError($"no member `{m.Member}`", Z42Type.Error, m.Span);
        }
        if (target.Type is Z42ClassType ct)
        {
            bool insideClass = env.CurrentClass == ct.Name;
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
        // L3-G2 / G2.5: type parameter member access — resolve via base class first, then constraint interfaces.
        if (target.Type is Z42GenericParamType gp)
        {
            // Field-stored T may have null constraints; consult active where-clause scope.
            // L3-G2.5 bare-typeparam: LookupEffectiveConstraints merges one hop through U: T.
            var bundle = (gp.BaseClassConstraint, gp.InterfaceConstraints) switch
            {
                (null, null) => _symbols.LookupEffectiveConstraints(gp.Name),
                _            => _symbols.LookupEffectiveConstraints(gp.Name) is { IsEmpty: false } scoped
                                ? scoped  // active scope wins when it exists (may have TypeParamConstraint hop)
                                : new GenericConstraintBundle(gp.BaseClassConstraint,
                                      gp.InterfaceConstraints ?? []),
            };
            if (bundle.BaseClass is { } bc)
            {
                if (bc.Fields.TryGetValue(m.Member, out var ft))
                    return new BoundMember(target, m.Member, ft, m.Span);
                if (bc.Methods.TryGetValue(m.Member, out var mt))
                    return new BoundMember(target, m.Member, mt, m.Span);
            }
            foreach (var iface in bundle.Interfaces)
                if (iface.Methods.TryGetValue(m.Member, out var cfmt))
                    return new BoundMember(target, m.Member, cfmt, m.Span);

            _diags.Error(DiagnosticCodes.TypeMismatch,
                bundle.IsEmpty
                    ? $"unconstrained type parameter `{gp.Name}` has no member `{m.Member}`; add a `where {gp.Name}: ...` clause"
                    : $"type parameter `{gp.Name}` has no member `{m.Member}` in its constraints",
                m.Span);
            return new BoundError($"no member `{m.Member}` on `{gp.Name}`", Z42Type.Error, m.Span);
        }
        if (m.Member is "Length" && (target.Type is Z42ArrayType || target.Type == Z42Type.String))
            return new BoundMember(target, m.Member, Z42Type.Int, m.Span);
        if (m.Member == "Count")
            return new BoundMember(target, m.Member, Z42Type.Int, m.Span);
        return new BoundMember(target, m.Member, Z42Type.Unknown, m.Span);
    }

}
