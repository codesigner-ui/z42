using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// Call binding and argument checking — part of the TypeChecker partial class.
public sealed partial class TypeChecker
{
    // ── Call ──────────────────────────────────────────────────────────────────

    private BoundCall BindCall(CallExpr call, TypeEnv env)
    {
        // ── User-defined static class method: ClassName.Method(args) ──────────
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var clsName }, Member: var staticMember }
            && _symbols.Classes.TryGetValue(clsName, out var staticCt))
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
                && !_symbols.Classes.ContainsKey(tgtName) && !_symbols.Interfaces.ContainsKey(tgtName)
                && !_symbols.EnumTypes.Contains(tgtName)
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
                    bool isVirtual = _symbols.VirtualMethods.TryGetValue(ct.Name, out var vmSet)
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
            && _symbols.Classes.TryGetValue(_currentClass, out var curCt)
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
