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
        // ── Static class method: ClassName.Method(args) ──────────────────────
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var clsName }, Member: var staticMember }
            && _symbols.Classes.TryGetValue(clsName, out var staticCt))
        {
            bool isImported = _symbols.ImportedClassNames.Contains(clsName);
            var arityKey  = $"{staticMember}${call.Args.Count}";
            var staticSig = staticCt.StaticMethods.TryGetValue(staticMember, out var s1) ? s1
                          : staticCt.StaticMethods.TryGetValue(arityKey,     out var s2) ? s2 : null;
            if (staticSig is not null)
            {
                var args = call.Args.Select(a => BindExpr(a, env)).ToList();
                CheckArgCount(args.Count, staticSig.MinArgCount, staticSig.Params.Count, call.Span);
                CheckArgTypes(call.Args, args, staticSig.Params);
                return new BoundCall(BoundCallKind.Static, null, clsName, staticMember,
                    null, args, staticSig.Ret, call.Span);
            }
            // Imported class: method not found → try DepIndex, or defer to IrGen
            if (isImported)
            {
                var args = call.Args.Select(a => BindExpr(a, env)).ToList();
                if (_depIndex?.TryGetStatic(clsName, staticMember, out var depStaticEntry) == true)
                {
                    var retType = IrTypeToZ42Type(depStaticEntry.RetType);
                    return new BoundCall(BoundCallKind.Static, null, clsName, staticMember,
                        null, args, retType, call.Span);
                }
                // Method not found in DepIndex either — return Unknown type, IrGen will resolve
                return new BoundCall(BoundCallKind.Static, null, clsName, staticMember,
                    null, args, Z42Type.Unknown, call.Span);
            }
        }

        // ── Member call ───────────────────────────────────────────────────────
        if (call.Callee is MemberExpr mCallee)
        {
            // Unknown target: stdlib/external class (e.g. Console, Assert, Math, string, int, double)
            if (mCallee.Target is IdentExpr { Name: var tgtName }
                && !_symbols.Classes.ContainsKey(tgtName) && !_symbols.Interfaces.ContainsKey(tgtName)
                && !_symbols.EnumTypes.Contains(tgtName)
                && env.LookupVar(tgtName) == null && env.LookupFunc(tgtName) == null)
            {
                var args = call.Args.Select(a => BindExpr(a, env)).ToList();

                // Map built-in type names to stdlib class names
                string resolvedClassName = tgtName switch
                {
                    "string" => "Std.String",
                    "int" => "Std.Int",
                    "double" => "Std.Double",
                    "bool" => "Std.Bool",
                    _ => tgtName
                };

                // Try to resolve via DepIndex (static method on stdlib class)
                if (_depIndex?.TryGetStatic(resolvedClassName, mCallee.Member, out var depUnknownEntry) == true)
                {
                    var retType = IrTypeToZ42Type(depUnknownEntry.RetType);
                    return new BoundCall(BoundCallKind.Static, null, resolvedClassName, mCallee.Member,
                        null, args, retType, call.Span);
                }
                // Not found in DepIndex — return Unknown type with resolved class name
                // so that IrGen can still use the correct qualified name if available
                return new BoundCall(BoundCallKind.Static, null, resolvedClassName, mCallee.Member,
                    null, args, Z42Type.Unknown, call.Span);
            }

            var recvExpr = BindExpr(mCallee.Target, env);
            var argBound = call.Args.Select(a => BindExpr(a, env)).ToList();

            if (recvExpr.Type is Z42ClassType ct)
            {
                var instArityKey = $"{mCallee.Member}${argBound.Count}";
                bool byBare  = ct.Methods.TryGetValue(mCallee.Member, out var mt1);
                Z42FuncType? mt2 = null;
                bool byArity = !byBare && ct.Methods.TryGetValue(instArityKey, out mt2);
                var mt = byBare ? mt1 : byArity ? mt2 : null;
                // Use arity-suffixed name when the method was resolved via arity key
                string resolvedMethodName = byArity ? instArityKey : mCallee.Member;
                bool isImportedCls = _symbols.ImportedClassNames.Contains(ct.Name);
                if (mt is not null)
                {
                    bool insideClass = env.CurrentClass == ct.Name;
                    var visKey = ct.MemberVisibility.ContainsKey(mCallee.Member)
                        ? mCallee.Member : instArityKey;
                    if (!insideClass
                        && ct.MemberVisibility.TryGetValue(visKey, out var mv)
                        && mv == Visibility.Private)
                        _diags.Error(DiagnosticCodes.AccessViolation,
                            $"method `{mCallee.Member}` is private to `{ct.Name}`", call.Span);
                    CheckArgCount(argBound.Count, mt.MinArgCount, mt.Params.Count, call.Span);
                    CheckArgTypes(call.Args, argBound, mt.Params);
                    // Imported classes always use Instance method calls (no virtual dispatch in stdlib).
                    // User-defined classes may use Virtual dispatch.
                    bool isVirtual = !isImportedCls
                        && _symbols.VirtualMethods.TryGetValue(ct.Name, out var vmSet)
                        && (vmSet.Contains(mCallee.Member) || vmSet.Contains(instArityKey));
                    return new BoundCall(
                        isVirtual ? BoundCallKind.Virtual : BoundCallKind.Instance,
                        recvExpr, ct.Name, resolvedMethodName, null, argBound, mt.Ret, call.Span);
                }
                // Imported class: method not found → try DepIndex, or defer to IrGen
                if (isImportedCls)
                {
                    // Try instance method lookup: paramCount = argBound.Count (user args, excluding 'this')
                    if (_depIndex?.TryGetInstance(mCallee.Member, argBound.Count, out var depInstanceEntry) == true)
                    {
                        var retType = IrTypeToZ42Type(depInstanceEntry.RetType);
                        return new BoundCall(BoundCallKind.Instance, recvExpr, ct.Name, mCallee.Member,
                            null, argBound, retType, call.Span);
                    }
                    // Not found in DepIndex — return Unknown type, IrGen will attempt to resolve
                    return new BoundCall(BoundCallKind.Instance, recvExpr, ct.Name, mCallee.Member,
                        null, argBound, Z42Type.Unknown, call.Span);
                }
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type `{ct.Name}` has no method `{mCallee.Member}`", call.Span);
                return new BoundCall(BoundCallKind.Instance, recvExpr, ct.Name, mCallee.Member,
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
                return new BoundCall(BoundCallKind.Virtual, recvExpr, ifaceType.Name, mCallee.Member,
                    null, argBound, Z42Type.Error, call.Span);
            }

            // Primitive/unknown type (e.g. List, Dictionary, Array, string, int, etc.)
            // For known builtin collection types, pass ReceiverClass so Codegen can use BuiltinInstr
            string? primitiveClassName = null;
            if (recvExpr.Type is Z42PrimType pt && IsBuiltinCollectionType(pt.Name))
                primitiveClassName = pt.Name;

            if (_depIndex?.TryGetInstance(mCallee.Member, argBound.Count, out var depPrimitiveEntry) == true)
            {
                var retType = IrTypeToZ42Type(depPrimitiveEntry.RetType);
                return new BoundCall(BoundCallKind.Instance, recvExpr, primitiveClassName, mCallee.Member,
                    null, argBound, retType, call.Span);
            }
            // Still unresolved — virtual dispatch fallback
            return new BoundCall(BoundCallKind.Instance, recvExpr, primitiveClassName, mCallee.Member,
                null, argBound, Z42Type.Unknown, call.Span);
        }

        // ── Free function call ────────────────────────────────────────────────
        var freeArgs = call.Args.Select(a => BindExpr(a, env)).ToList();

        // Bare name inside current class's static methods
        if (call.Callee is IdentExpr { Name: var bareCallName }
            && env.CurrentClass != null
            && _symbols.Classes.TryGetValue(env.CurrentClass, out var curCt)
            && curCt.StaticMethods.TryGetValue(bareCallName, out var bareSig))
        {
            CheckArgCount(freeArgs.Count, bareSig.MinArgCount, bareSig.Params.Count, call.Span);
            CheckArgTypes(call.Args, freeArgs, bareSig.Params);
            return new BoundCall(BoundCallKind.Static, null, env.CurrentClass, bareCallName,
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
            // Unknown function — report error
            BindIdent(funcId, env);
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"undefined function or variable `{funcId.Name}`", funcId.Span);
            return new BoundCall(BoundCallKind.Free, null, null, null,
                funcId.Name, freeArgs, Z42Type.Error, call.Span);
        }

        // Non-identifier callee (rare)
        var calleeExpr = BindExpr(call.Callee, env);
        if (calleeExpr.Type is Z42FuncType ft2)
        {
            CheckArgCount(freeArgs.Count, ft2.MinArgCount, ft2.Params.Count, call.Span);
            CheckArgTypes(call.Args, freeArgs, ft2.Params);
            return new BoundCall(BoundCallKind.Free, null, null, null,
                null, freeArgs, ft2.Ret, call.Span);
        }
        _diags.Error(DiagnosticCodes.TypeMismatch,
            $"cannot call non-function type `{calleeExpr.Type}`", call.Callee.Span);
        return new BoundCall(BoundCallKind.Free, null, null, null,
            null, freeArgs, Z42Type.Error, call.Span);
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

    /// Check if a type name is a builtin collection type (NOT string, int, etc.)
    private bool IsBuiltinCollectionType(string typeName)
    {
        return typeName is "List" or "Dictionary" or "Array" or "StringBuilder";
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
