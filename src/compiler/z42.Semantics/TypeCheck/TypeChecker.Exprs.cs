using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// Expression dispatcher + simple bindings (literal / identifier / unary / op
/// converters) — part of the TypeChecker partial class.
///
/// Pairs with `TypeChecker.Exprs.Operators.cs` (assignment + binary) and
/// `TypeChecker.Exprs.Members.cs` (member access + ctor resolution).
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

            // ModifiedArg (spec: define-ref-out-in-parameters) — only valid
            // as an argument to a CallExpr / NewExpr; appearing anywhere else
            // is a parser-side mistake (parser ensures this). Inner is bound
            // here; modifier validation against the callee's signature happens
            // in CheckArgTypes (TypeChecker.Calls.cs). For `out var x`, the
            // local is declared with placeholder Unknown type; CheckArgTypes
            // patches the type once the matching parameter is resolved.
            case ModifiedArg ma:    return BindModifiedArg(ma, env);
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

            case DefaultExpr de:
            {
                // add-default-expression (2026-05-06): resolve T through the
                // standard ResolveType path then validate it.
                //
                // - Generic type-parameter (Z42GenericParamType) → E0421 Phase 2 deferred
                // - ResolveType fall-through emits Z42PrimType("<unknown>") for any
                //   unknown NamedType (catch-all branch in SymbolCollector); we
                //   detect that by cross-checking against the TypeRegistry of known
                //   primitives + class / interface / enum / delegate maps. If none
                //   match, the type isn't real → E0421 type-not-found.
                var t = ResolveType(de.Target);
                if (t is Z42GenericParamType gp)
                {
                    _diags.Error(DiagnosticCodes.InvalidDefaultType,
                        $"default(<{gp.Name}>) on generic type parameter is not yet supported " +
                        "(deferred to spec add-default-generic-typeparam — see docs/deferred.md D-8b-3 Phase 2)",
                        de.Span);
                    return new BoundDefault(Z42Type.Error, de.Span);
                }
                if (!IsResolvedDefaultTarget(t))
                {
                    var targetName = de.Target switch
                    {
                        NamedType nt   => nt.Name,
                        GenericType gt => gt.Name,
                        _              => t.ToString() ?? "<unknown>",
                    };
                    _diags.Error(DiagnosticCodes.InvalidDefaultType,
                        $"default(<{targetName}>): type not found", de.Span);
                    return new BoundDefault(Z42Type.Error, de.Span);
                }
                return new BoundDefault(t, de.Span);
            }

            case NewExpr newExpr:
            {
                var args    = newExpr.Args.Select(a => BindExpr(a, env)).ToList();
                var newType = ResolveType(newExpr.Type);
                // 2026-05-07 add-class-arity-overloading: qualName must reflect
                // the actual class IrName (mangled `Foo$N` when collision exists),
                // so downstream ctor lookup / IR emit see the right class.
                // For non-collision classes IrName == Name → no behavior change.
                var resolvedClass = newType switch
                {
                    Z42InstantiatedType inst => inst.Definition,
                    Z42ClassType ct          => ct,
                    _                        => null,
                };
                var qualName = resolvedClass?.IrName
                              ?? newExpr.Type switch
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
                    && (_symbols.Classes.TryGetValue($"{gt2.Name}${gt2.TypeArgs.Count}", out var clsType)
                        || _symbols.Classes.TryGetValue(gt2.Name, out clsType))
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
                        ValidateGenericConstraints(clsType.IrName, clsType.TypeParams, resolved,
                            _classConstraints, newExpr.Span);
                    }
                }
                var ctorName = ResolveCtorName(qualName, args.Count, newExpr.Span);
                return new BoundNew(qualName, ctorName, args, newType, newExpr.Span);
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

            case LambdaExpr lambda:
                return BindLambda(lambda, env, expectedType);

            case ErrorExpr e:
                return new BoundError("error expr", Z42Type.Error, e.Span);

            default:
                return new BoundError("unknown expr", Z42Type.Unknown, expr.Span);
        }
    }

    // ── Lambda ────────────────────────────────────────────────────────────────

    /// L2 lambda binding: synthesize a `Z42FuncType`, bind the body, and verify
    /// no captures (closure captures are L3-only). See docs/design/closure.md.
    private BoundExpr BindLambda(LambdaExpr lambda, TypeEnv env, Z42Type? expected)
    {
        // 1. Determine each param's resolved type.
        var expectedFn = expected as Z42FuncType;
        if (expectedFn != null && expectedFn.Params.Count != lambda.Params.Count)
        {
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"lambda has {lambda.Params.Count} parameters but expected {expectedFn.Params.Count}",
                lambda.Span);
            return new BoundError("lambda arity mismatch", Z42Type.Error, lambda.Span);
        }

        var boundParams = new List<BoundLambdaParam>(lambda.Params.Count);
        for (int i = 0; i < lambda.Params.Count; i++)
        {
            var p = lambda.Params[i];
            Z42Type ptype;
            if (p.Type != null)
            {
                ptype = ResolveType(p.Type);
            }
            else if (expectedFn != null)
            {
                ptype = expectedFn.Params[i];
            }
            else
            {
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"cannot infer type for lambda parameter `{p.Name}`; either provide an explicit type (`(int {p.Name}) => ...`) or assign to a variable with a known function type",
                    p.Span);
                ptype = Z42Type.Error;
            }
            boundParams.Add(new BoundLambdaParam(p.Name, ptype, p.Span));
        }

        // 2. Push lambda scope: params become locals; outer env is the capture boundary.
        var lambdaEnv = env.PushScope();
        foreach (var bp in boundParams)
            lambdaEnv.Define(bp.Name, bp.Type);

        var frame = new LambdaBindingFrame { OuterEnv = env };
        _lambdaBindingStack.Push(frame);
        BoundLambdaBody body;
        Z42Type retType;
        try
        {
            switch (lambda.Body)
            {
                case LambdaExprBody eb:
                {
                    var be = BindExpr(eb.Expr, lambdaEnv, expectedFn?.Ret);
                    body    = new BoundLambdaExprBody(be, eb.Span);
                    retType = be.Type;
                    break;
                }
                case LambdaBlockBody bb:
                {
                    // Block body: use the expected return type if known, else Unknown.
                    var ret  = expectedFn?.Ret ?? Z42Type.Unknown;
                    var blk  = BindBlock(bb.Block, lambdaEnv, ret);
                    body     = new BoundLambdaBlockBody(blk, bb.Span);
                    retType  = ret;
                    break;
                }
                default:
                    return new BoundError("invalid lambda body", Z42Type.Error, lambda.Span);
            }
        }
        finally
        {
            _lambdaBindingStack.Pop();
        }

        // 3. Synthesize the function type.
        var paramTypes = boundParams.Select(p => p.Type).ToList();
        var fnType     = new Z42FuncType(paramTypes, retType);
        return new BoundLambda(boundParams, body, fnType, frame.Captures, lambda.Span);
    }

    // ── Identifier ────────────────────────────────────────────────────────────

    private BoundExpr BindIdent(IdentExpr id, TypeEnv env)
    {
        var varType = env.LookupVar(id.Name);
        if (varType != null)
        {
            // L3 capture path (impl-closure-l3-core): when inside one or more
            // nested lambdas / local-fn bodies, a name that resolves through
            // any enclosing lambda's outer boundary must be captured by *each*
            // such enclosing frame so the capture propagates transitively from
            // the outermost defining scope down to the innermost reference.
            // See docs/design/closure.md §4 + impl-closure-l3-core Decision 6.
            if (_lambdaBindingStack.Count > 0)
            {
                var topFrame = _lambdaBindingStack.Peek();
                if (!env.ResolvesVarBelowBoundary(id.Name, topFrame.OuterEnv))
                {
                    // Walk frames from outermost → innermost so each frame
                    // whose boundary excludes the variable also captures it.
                    int innermostIndex = -1;
                    // Spec define-ref-out-in-parameters: lambda may not capture
                    // ref/out/in parameters (Decision 4 / spec scenario "lambda
                    // 不能捕获 ref/out/in 参数"). The captured slot is a frame
                    // address living only for the call stack; closing over it
                    // would let the address escape the call frame.
                    var capturedMod = env.LookupParamModifier(id.Name);
                    if (capturedMod != ParamModifier.None)
                    {
                        _diags.Error(DiagnosticCodes.TypeMismatch,
                            $"lambda cannot capture `{ModifierKeyword(capturedMod)}` parameter `{id.Name}`",
                            id.Span);
                        // Continue building bound tree as if not captured —
                        // downstream traversal stays sane.
                    }
                    var kind = Z42Type.IsReferenceType(varType)
                        ? BoundCaptureKind.ReferenceShare
                        : BoundCaptureKind.ValueSnapshot;
                    foreach (var frame in _lambdaBindingStack.Reverse())
                    {
                        // The frame must capture iff the name is visible from
                        // its outer (boundary) env — i.e. lives at or above
                        // the boundary so the lifted body can't see it directly.
                        if (frame.OuterEnv.LookupVar(id.Name) != null)
                        {
                            if (!frame.NameToIndex.TryGetValue(id.Name, out var idx))
                            {
                                idx = frame.Captures.Count;
                                frame.Captures.Add(new BoundCapture(
                                    id.Name, varType, kind, id.Span));
                                frame.NameToIndex[id.Name] = idx;
                            }
                            innermostIndex = idx;
                        }
                    }
                    if (innermostIndex >= 0)
                        return new BoundCapturedIdent(id.Name, varType, innermostIndex, id.Span);
                }
            }
            return new BoundIdent(id.Name, varType, id.Span);
        }
        var fnType = env.LookupFunc(id.Name);
        if (fnType != null) return new BoundIdent(id.Name, fnType, id.Span);
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

    /// add-default-expression (2026-05-06): is `t` a valid `default(T)` target?
    ///
    /// Only types we can produce a zero-value for at codegen time qualify.
    /// `Z42PrimType` of an *unknown* name (the SymbolCollector ResolveType
    /// catch-all) is **not** valid — that's the type-not-found path the caller
    /// turns into E0421. Reject Void / Error / Null / Unknown explicitly so
    /// the user gets a clear error rather than silent ConstNull emission.
    private bool IsResolvedDefaultTarget(Z42Type t)
    {
        switch (t)
        {
            case Z42PrimType pt:
                return TypeRegistry.IsPrimitiveType(pt.Name);
            case Z42ClassType:
            case Z42InterfaceType:
            case Z42ArrayType:
            case Z42OptionType:
            case Z42EnumType:
            case Z42FuncType:
            case Z42InstantiatedType:
                return true;
            default:
                return false;  // Z42VoidType / Z42ErrorType / Z42NullType / Z42UnknownType
        }
    }
}
