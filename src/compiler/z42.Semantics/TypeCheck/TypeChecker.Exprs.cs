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
                // L3-G4e: user-defined indexer — dispatch to get_Item on class receivers.
                if (TryBindIndexerGet(target, index, ix.Span) is { } getCall)
                    return getCall;
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
                        // L3-G3d: _classConstraints also covers imported classes via TSIG.
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
        // L3-G4e: `obj[i] = v` on a class receiver with set_Item → dispatch to setter.
        // Detect BEFORE binding target (which would go through get_Item for class receiver).
        if (assign.Target is IndexExpr ixTgt)
        {
            var recv   = BindExpr(ixTgt.Target, env);
            var idx    = BindExpr(ixTgt.Index,  env);
            if (TryFindSetter(recv.Type, out var setter, out var className))
            {
                var value = BindExpr(assign.Value, env);
                RequireAssignable(setter!.Params[^1], value.Type, assign.Value.Span);
                var args = new List<BoundExpr> { idx, value };
                return new BoundCall(BoundCallKind.Virtual, recv, className, "set_Item",
                    null, args, value.Type, assign.Span);
            }
        }

        var target    = BindExpr(assign.Target, env);
        var intLitVal = ExtractIntLiteralValue(assign.Value);
        BoundExpr value2;
        if (intLitVal != null)
        {
            var rangeOk = TryCheckIntLiteralRange(target.Type, intLitVal.Value, assign.Value.Span);
            value2 = BindExpr(assign.Value, env, target.Type);
            if (rangeOk == null)
                RequireAssignable(target.Type, value2.Type, assign.Value.Span);
        }
        else
        {
            value2 = BindExpr(assign.Value, env);
            RequireAssignable(target.Type, value2.Type, assign.Value.Span);
        }
        // Narrow from Unknown after first assignment
        if (assign.Target is IdentExpr id && target.Type is Z42UnknownType)
            env.Define(id.Name, value2.Type);
        return new BoundAssign(target, value2, value2.Type, assign.Span);
    }

    /// L3-G4e: if `recvType` is a class/instantiated class with `get_Item`, bind
    /// `obj[idx]` as a Virtual call to that method (with type-param substitution
    /// for instantiated generics). Returns null otherwise.
    private BoundExpr? TryBindIndexerGet(BoundExpr recv, BoundExpr index, Span span)
    {
        if (TryFindIndexer(recv.Type, "get_Item", out var mt, out var className, out var subMap))
        {
            var retType = SubstituteTypeParams(mt!.Ret, subMap);
            return new BoundCall(BoundCallKind.Virtual, recv, className, "get_Item",
                null, new List<BoundExpr> { index }, retType, span);
        }
        return null;
    }

    /// Tries to locate a `set_Item` on a class/instantiated class receiver, returning
    /// the substituted param list's value type via `setter.Params[^1]`.
    private bool TryFindSetter(Z42Type recvType,
                               out Z42FuncType? setter,
                               out string? className)
    {
        if (TryFindIndexer(recvType, "set_Item", out var mt, out var cls, out var subMap))
        {
            setter    = (Z42FuncType)SubstituteTypeParams(mt!, subMap);
            className = cls;
            return true;
        }
        setter = null; className = null; return false;
    }

    private bool TryFindIndexer(Z42Type recvType, string name,
                                out Z42FuncType? method,
                                out string? className,
                                out IReadOnlyDictionary<string, Z42Type>? subMap)
    {
        method = null; className = null; subMap = null;
        Z42ClassType? def = null;
        switch (recvType)
        {
            case Z42ClassType ct:        def = ct; break;
            case Z42InstantiatedType it: def = it.Definition; subMap = BuildSubstitutionMap(it); break;
        }
        if (def is null) return false;
        if (!def.Methods.TryGetValue(name, out var mt)) return false;
        method = mt;
        className = def.Name;
        return true;
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

        // L3 operator overload: `a + b` → call static `op_Add(a, b)` on a class/struct
        // or instance `a.op_Add(b)` on generic-param / class when static form absent.
        if (TryBindOperatorCall(bin.Op, left, right, bin.Span) is { } opCall)
            return opCall;

        if (!BinaryTypeTable.Rules.TryGetValue(bin.Op, out var rule))
            return new BoundBinary(op, left, right, Z42Type.Unknown, bin.Span);

        CheckBinaryOperand(rule.LeftOk,  rule.Requirement, left.Type,  bin.Left.Span,  bin.Op);
        CheckBinaryOperand(rule.RightOk, rule.Requirement, right.Type, bin.Right.Span, bin.Op);

        var outType = (left.Type is Z42ErrorType || right.Type is Z42ErrorType)
            ? Z42Type.Error : rule.Output(left.Type, right.Type);
        return new BoundBinary(op, left, right, outType, bin.Span);
    }

    /// L3 operator overload: try resolving `a <op> b` to an operator method call.
    /// Priority:
    ///   1. Static `op_<Name>(a, b)` on left.Type (or right.Type) — C# operator overload
    ///   2. Instance `a.op_<Name>(b)` via left.Type's Methods / constraint interfaces —
    ///      INumber / user-class instance operator
    /// Returns null when neither form matches; caller falls back to BinaryTypeTable.
    private BoundExpr? TryBindOperatorCall(string op, BoundExpr left, BoundExpr right, Span span)
    {
        string? methodName = op switch
        {
            "+" => "op_Add",
            "-" => "op_Subtract",
            "*" => "op_Multiply",
            "/" => "op_Divide",
            "%" => "op_Modulo",
            _   => null,
        };
        if (methodName is null) return null;

        // Skip overload when both sides are plain primitive numeric — let BinaryTypeTable
        // emit the IR AddInstr fast path (no method dispatch).
        if (left.Type is Z42PrimType && right.Type is Z42PrimType
            && Z42Type.IsNumeric(left.Type) && Z42Type.IsNumeric(right.Type))
            return null;

        // 1. Static operator method on either side's class. Signature must match (L, R).
        if (TryLookupStaticOperator(left.Type, methodName, left.Type, right.Type) is { } ls)
            return new BoundCall(BoundCallKind.Static, null, ls.ClassName, methodName,
                null, [left, right], ls.ReturnType, span);
        if (TryLookupStaticOperator(right.Type, methodName, left.Type, right.Type) is { } rs)
            return new BoundCall(BoundCallKind.Static, null, rs.ClassName, methodName,
                null, [left, right], rs.ReturnType, span);

        // 2. Instance op_<Name> on left.Type — covers user classes implementing INumber
        //    directly, and generic params whose constraints supply op_<Name>.
        if (TryLookupInstanceOperator(left.Type, methodName, right.Type) is { } li)
            return new BoundCall(BoundCallKind.Virtual, left, li.ClassName, methodName,
                null, [right], li.ReturnType, span);

        return null;
    }

    private (string ClassName, Z42Type ReturnType)? TryLookupStaticOperator(
        Z42Type t, string methodName, Z42Type leftArg, Z42Type rightArg)
    {
        string? className = t switch
        {
            Z42ClassType ct         => ct.Name,
            Z42InstantiatedType inst => inst.Definition.Name,
            _                        => null,
        };
        if (className is null) return null;
        if (!_symbols.Classes.TryGetValue(className, out var classType)) return null;
        if (!classType.StaticMethods.TryGetValue(methodName, out var sig)) return null;
        if (sig.Params.Count != 2) return null;
        // Signature match: both arguments must be assignable to the declared params.
        if (!Z42Type.IsAssignableTo(sig.Params[0], leftArg)) return null;
        if (!Z42Type.IsAssignableTo(sig.Params[1], rightArg)) return null;
        return (className, ResolveStubType(sig.Ret));
    }

    /// Normalize a stub Z42ClassType (returned during first-pass signature collection,
    /// before class shapes were populated) to the fully-populated Z42ClassType from
    /// `_symbols.Classes`. Needed when operator method signatures reference their
    /// own enclosing class as return type.
    private Z42Type ResolveStubType(Z42Type t) =>
        t is Z42ClassType ct && _symbols.Classes.TryGetValue(ct.Name, out var full) ? full : t;

    private (string ClassName, Z42Type ReturnType)? TryLookupInstanceOperator(
        Z42Type t, string methodName, Z42Type rightArg)
    {
        // Concrete class / struct — instance method takes 1 param (other).
        if (t is Z42ClassType ct
            && _symbols.Classes.TryGetValue(ct.Name, out var classType)
            && classType.Methods.TryGetValue(methodName, out var mt)
            && mt.Params.Count == 1
            && Z42Type.IsAssignableTo(mt.Params[0], rightArg))
            return (ct.Name, ResolveStubType(mt.Ret));
        if (t is Z42InstantiatedType inst
            && inst.Definition.Methods.TryGetValue(methodName, out var instMt)
            && instMt.Params.Count == 1)
        {
            var subMap = BuildSubstitutionMap(inst);
            var substParam = SubstituteTypeParams(instMt.Params[0], subMap);
            if (!Z42Type.IsAssignableTo(substParam, rightArg)) return null;
            return (inst.Definition.Name, SubstituteTypeParams(instMt.Ret, subMap));
        }
        // Generic parameter — look through interface constraints (INumber<T> etc.)
        // For `a + b` where a: T, b: T (homogeneous), the constraint interface's
        // method takes T and returns T; signature check is trivially satisfied.
        if (t is Z42GenericParamType gp)
        {
            // Collect both the type-param's own InterfaceConstraints (built during
            // class/function signature collection) and the active where-clause bundle.
            var ifaces = new List<Z42InterfaceType>();
            if (gp.InterfaceConstraints is { } direct) ifaces.AddRange(direct);
            var bundle = _symbols.LookupEffectiveConstraints(gp.Name);
            foreach (var iface in bundle.Interfaces)
                if (!ifaces.Any(i => i.Name == iface.Name)) ifaces.Add(iface);

            foreach (var iface in ifaces)
                if (iface.Methods.TryGetValue(methodName, out var gmt) && gmt.Params.Count == 1)
                    // Substitute `T` → `gp` in return type (e.g. INumber<T>.op_Add returns T,
                    // which should become the generic param itself in the caller scope).
                    return (iface.Name, SubstituteGenericReturnType(gmt.Ret, gp));
        }
        return null;
    }

    /// For `where T: INumber<T>` and `a.op_Add(b)` with ret `T`, substitute T → caller's
    /// generic param reference. For interface method return types that are Z42GenericParamType
    /// or Z42PrimType("T"), replace with the caller's generic-param instance.
    private static Z42Type SubstituteGenericReturnType(Z42Type ret, Z42GenericParamType callerGp) =>
        ret switch
        {
            Z42GenericParamType          => callerGp,
            Z42PrimType p when p.Name == callerGp.Name => callerGp,
            _                             => ret,
        };

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
