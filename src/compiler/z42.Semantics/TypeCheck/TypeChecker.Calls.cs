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
            // Spec define-ref-out-in-parameters (Decision 7): modifier-aware overload.
            var (staticSig, staticKey) = LookupMethodOverload(staticCt.StaticMethods, staticMember, call.Args);
            if (staticSig is not null)
            {
                var args = call.Args.Select(a => BindExpr(a, env)).ToList();
                CheckArgCount(args.Count, staticSig.MinArgCount, staticSig.Params.Count, call.Span);
                // 2026-05-05 fix-generic-extern-infer: substitute T → concrete
                // before arg-type validation so `BlackBox<T>(int)` etc. accept
                // int args. Same path also handles non-extern generic statics.
                var checkedParams = SubstituteGenericParams(staticSig, args);
                CheckArgTypes(call.Args, args, checkedParams);
                CheckArgModifiers(call.Args, args, staticSig, env, call.Span);
                var inferredRet = SubstituteGenericReturn(staticSig, args);
                // Use resolved key when overload (modifier-tagged or arity-tagged) so
                // IR Codegen / VM dispatch resolves to the correct variant.
                string resolvedStatic = staticKey != staticMember ? staticKey! : staticMember;
                return new BoundCall(BoundCallKind.Static, null, clsName, resolvedStatic,
                    null, args, inferredRet, call.Span);
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
            // Unknown target: stdlib/external class (e.g. Console, Assert, Math, string, int, double).
            // Primitive keywords (int/double/bool/string/...) are force-routed through this branch
            // even when a struct with the same short name is registered — they're compiler-magic
            // identifiers, and the DepIndex holds their static methods under the stdlib class name.
            bool isPrimitiveIdent = mCallee.Target is IdentExpr { Name: var pn }
                && pn is "int" or "long" or "short" or "byte" or "sbyte"
                        or "uint" or "ulong" or "ushort"
                        or "double" or "float" or "bool" or "char" or "string";
            if (mCallee.Target is IdentExpr { Name: var tgtName }
                && (isPrimitiveIdent
                    || (!_symbols.Classes.ContainsKey(tgtName) && !_symbols.Interfaces.ContainsKey(tgtName)
                        && !_symbols.EnumTypes.Contains(tgtName)
                        && env.LookupVar(tgtName) == null && env.LookupFunc(tgtName) == null)))
            {
                var args = call.Args.Select(a => BindExpr(a, env)).ToList();

                // Map built-in type names to stdlib class names.
                // L3-G4b primitive-as-struct: int / double now live as `struct int` / `struct double`
                // in Std namespace (lowercase). `bool` / `string` still resolve to uppercase helpers
                // for backward compat with Std.Bool / Std.String static members (IsNullOrEmpty, etc.).
                string resolvedClassName = tgtName switch
                {
                    "string" => "Std.String",
                    "int"    => "Std.int",
                    "double" => "Std.double",
                    "bool"   => "Std.bool",
                    "char"   => "Std.char",
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

            // L3-G4a: method call on instantiated generic class — substitute type params in sig.
            if (recvExpr.Type is Z42InstantiatedType inst)
            {
                var subMap = BuildSubstitutionMap(inst);
                var def    = inst.Definition;
                var instArityKey = $"{mCallee.Member}${argBound.Count}";
                bool byBare  = def.Methods.TryGetValue(mCallee.Member, out var mt1);
                Z42FuncType? mt2 = null;
                bool byArity = !byBare && def.Methods.TryGetValue(instArityKey, out mt2);
                var mtRaw = byBare ? mt1 : byArity ? mt2 : null;
                string resolvedMethodName = byArity ? instArityKey : mCallee.Member;
                if (mtRaw is not null)
                {
                    bool insideClass = env.CurrentClass == def.Name;
                    var visKey = def.MemberVisibility.ContainsKey(mCallee.Member)
                        ? mCallee.Member : instArityKey;
                    if (!insideClass
                        && def.MemberVisibility.TryGetValue(visKey, out var mv)
                        && mv == Visibility.Private)
                        _diags.Error(DiagnosticCodes.AccessViolation,
                            $"method `{mCallee.Member}` is private to `{def.Name}`", call.Span);
                    var mtSub = (Z42FuncType)SubstituteTypeParams(mtRaw, subMap);
                    CheckArgCount(argBound.Count, mtSub.MinArgCount, mtSub.Params.Count, call.Span);
                    CheckArgTypes(call.Args, argBound, mtSub.Params);
                    CheckArgModifiers(call.Args, argBound, mtSub, env, call.Span);
                    bool isVirtual = _symbols.VirtualMethods.TryGetValue(def.Name, out var vmSet)
                        && (vmSet.Contains(mCallee.Member) || vmSet.Contains(instArityKey));
                    return new BoundCall(
                        isVirtual ? BoundCallKind.Virtual : BoundCallKind.Instance,
                        recvExpr, def.Name, resolvedMethodName, null, argBound, mtSub.Ret, call.Span);
                }
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type `{inst}` has no method `{mCallee.Member}`", call.Span);
                return new BoundCall(BoundCallKind.Instance, recvExpr, def.Name, mCallee.Member,
                    null, argBound, Z42Type.Error, call.Span);
            }

            if (recvExpr.Type is Z42ClassType ct)
            {
                var instArityKey = $"{mCallee.Member}${argBound.Count}";
                // Spec define-ref-out-in-parameters (Decision 7): modifier-aware overload.
                var (mt, instKey) = LookupMethodOverload(ct.Methods, mCallee.Member, call.Args);
                // Resolved method name for IR codegen — use the actual key found.
                string resolvedMethodName = instKey ?? mCallee.Member;
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
                    CheckArgModifiers(call.Args, argBound, mt, env, call.Span);
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
                    // Substitute generic interface T → concrete TypeArgs for method
                    // dispatch. `IEquatable<int>.Equals(T)` → `Equals(int)` etc.
                    var subMap = BuildInterfaceSubstitutionMap(ifaceType);
                    var imtSub = subMap is null ? imt
                                                : (Z42FuncType)SubstituteTypeParams(imt, subMap);
                    CheckArgCount(argBound.Count, imtSub.MinArgCount, imtSub.Params.Count, call.Span);
                    CheckArgTypes(call.Args, argBound, imtSub.Params);
                    CheckArgModifiers(call.Args, argBound, imtSub, env, call.Span);
                    return new BoundCall(BoundCallKind.Virtual, recvExpr, ifaceType.Name,
                        mCallee.Member, null, argBound, imtSub.Ret, call.Span);
                }
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"interface `{ifaceType.Name}` has no method `{mCallee.Member}`", call.Span);
                return new BoundCall(BoundCallKind.Virtual, recvExpr, ifaceType.Name, mCallee.Member,
                    null, argBound, Z42Type.Error, call.Span);
            }

            // L3-G2 / G2.5: generic type parameter receiver — dispatch via base class first, then interfaces.
            if (recvExpr.Type is Z42GenericParamType gp)
            {
                // Field-stored T may carry null constraints; consult active where-clause scope.
                // L3-G2.5 bare-typeparam: LookupEffectiveConstraints merges one hop through U: T.
                var bundle = (gp.BaseClassConstraint, gp.InterfaceConstraints) switch
                {
                    (null, null) => _symbols.LookupEffectiveConstraints(gp.Name),
                    _            => _symbols.LookupEffectiveConstraints(gp.Name) is { IsEmpty: false } scoped
                                    ? scoped
                                    : new GenericConstraintBundle(gp.BaseClassConstraint,
                                          gp.InterfaceConstraints ?? []),
                };
                if (bundle.BaseClass is { } bc
                    && bc.Methods.TryGetValue(mCallee.Member, out var bcMt))
                {
                    CheckArgCount(argBound.Count, bcMt.MinArgCount, bcMt.Params.Count, call.Span);
                    CheckArgTypes(call.Args, argBound, bcMt.Params);
                    CheckArgModifiers(call.Args, argBound, bcMt, env, call.Span);
                    return new BoundCall(BoundCallKind.Virtual, recvExpr, bc.Name,
                        mCallee.Member, null, argBound, bcMt.Ret, call.Span);
                }
                foreach (var iface in bundle.Interfaces)
                {
                    if (iface.Methods.TryGetValue(mCallee.Member, out var gmt))
                    {
                        CheckArgCount(argBound.Count, gmt.MinArgCount, gmt.Params.Count, call.Span);
                        CheckArgTypes(call.Args, argBound, gmt.Params);
                        CheckArgModifiers(call.Args, argBound, gmt, env, call.Span);
                        return new BoundCall(BoundCallKind.Virtual, recvExpr, iface.Name,
                            mCallee.Member, null, argBound, gmt.Ret, call.Span);
                    }
                    // L3 static abstract interface members: `x.op_Add(y)` on generic T
                    // whose constraint declares `static abstract T op_Add(T a, T b)`.
                    // VCall prepends receiver, so (x, y) args match the 2-param static sig.
                    if (iface.StaticMembers is { } sm
                        && sm.TryGetValue(mCallee.Member, out var staticMbr)
                        && staticMbr.Signature.Params.Count == argBound.Count + 1)
                    {
                        CheckArgTypes(call.Args, argBound,
                            staticMbr.Signature.Params.Skip(1).ToList());
                        var retType = SubstituteGenericReturnType(staticMbr.Signature.Ret, gp);
                        return new BoundCall(BoundCallKind.Virtual, recvExpr, iface.Name,
                            mCallee.Member, null, argBound, retType, call.Span);
                    }
                }
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    bundle.IsEmpty
                        ? $"unconstrained type parameter `{gp.Name}` has no method `{mCallee.Member}`; add a `where {gp.Name}: ...` clause"
                        : $"type parameter `{gp.Name}` has no method `{mCallee.Member}` in its constraints",
                    call.Span);
                return new BoundCall(BoundCallKind.Virtual, recvExpr, gp.Name, mCallee.Member,
                    null, argBound, Z42Type.Error, call.Span);
            }

            // Primitive/unknown type (e.g. List, Dictionary, Array, string, int, etc.)
            // For known builtin collection types, pass ReceiverClass so Codegen can use BuiltinInstr
            string? primitiveClassName = null;
            if (recvExpr.Type is Z42PrimType pt && IsBuiltinCollectionType(pt.Name))
                primitiveClassName = pt.Name;

            // L3 primitive-as-struct: when recvExpr is a primitive (e.g. string) and
            // a same-package stdlib class exists (String), consult its method table
            // so intra-package calls like `this.Substring(start, n)` resolve to the
            // correct overload `Substring$2` before the VM tries string-method lookup.
            if (recvExpr.Type is Z42PrimType primT
                && _symbols.Classes.TryGetValue(CapitalizeFirst(primT.Name), out var primClsType))
            {
                var primArityKey = $"{mCallee.Member}${argBound.Count}";
                bool primBare  = primClsType.Methods.TryGetValue(mCallee.Member, out var pmt1);
                Z42FuncType? pmt2 = null;
                bool primArity = !primBare && primClsType.Methods.TryGetValue(primArityKey, out pmt2);
                var pmt = primBare ? pmt1 : primArity ? pmt2 : null;
                if (pmt is not null)
                {
                    string resolved = primArity ? primArityKey : mCallee.Member;
                    CheckArgCount(argBound.Count, pmt.MinArgCount, pmt.Params.Count, call.Span);
                    CheckArgTypes(call.Args, argBound, pmt.Params);
                    CheckArgModifiers(call.Args, argBound, pmt, env, call.Span);
                    return new BoundCall(BoundCallKind.Instance, recvExpr, primitiveClassName,
                        resolved, null, argBound, pmt.Ret, call.Span);
                }
            }

            if (_depIndex?.TryGetInstance(mCallee.Member, argBound.Count, out var depPrimitiveEntry) == true)
            {
                var retType = IrTypeToZ42Type(depPrimitiveEntry.RetType);
                // If the imported signature is overloaded (QualifiedName ends in $N),
                // forward the arity-qualified method name so IR-level VCall hits the
                // right stdlib function (e.g. Std.String.Substring$2 not Substring).
                int lastDot = depPrimitiveEntry.QualifiedName.LastIndexOf('.');
                string resolvedName = lastDot >= 0
                    && depPrimitiveEntry.QualifiedName[(lastDot + 1)..].Contains('$')
                        ? depPrimitiveEntry.QualifiedName[(lastDot + 1)..]
                        : mCallee.Member;
                return new BoundCall(BoundCallKind.Instance, recvExpr, primitiveClassName, resolvedName,
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
            CheckArgModifiers(call.Args, freeArgs, bareSig, env, call.Span);
            return new BoundCall(BoundCallKind.Static, null, env.CurrentClass, bareCallName,
                null, freeArgs, bareSig.Ret, call.Span);
        }

        if (call.Callee is IdentExpr funcId)
        {
            // Spec define-ref-out-in-parameters (Decision 7): try modifier-tagged
            // overload key first when callsite has any modifier; fall back to bare.
            Z42FuncType? funcType = null;
            string resolvedFuncName = funcId.Name;
            var argModSig = ModifierMangling.PatternFromArgs(call.Args);
            if (!string.IsNullOrEmpty(argModSig))
            {
                var modKey = $"{funcId.Name}${call.Args.Count}${argModSig}";
                funcType = env.LookupFunc(modKey);
                if (funcType is not null) resolvedFuncName = modKey;
            }
            funcType ??= env.LookupFunc(funcId.Name);
            if (funcType is Z42FuncType ft)
            {
                CheckArgCount(freeArgs.Count, ft.MinArgCount, ft.Params.Count, call.Span);
                CheckArgTypes(call.Args, freeArgs, ft.Params);
                CheckArgModifiers(call.Args, freeArgs, ft, env, call.Span);
                // L3-G2: if the function has a constraint map, infer type args from params and validate.
                if (_funcConstraints.ContainsKey(funcId.Name))
                    InferAndValidateFuncConstraints(funcId.Name, ft, freeArgs, call.Span);
                // Infer type args from params for return type substitution (T → concrete).
                var retType = SubstituteGenericReturn(ft, freeArgs);
                return new BoundCall(BoundCallKind.Free, null, null, null,
                    resolvedFuncName, freeArgs, retType, call.Span);
            }
            // impl-lambda-l2: indirect call via a `Z42FuncType`-typed local variable
            // (e.g. `var f = x => x + 1; f(5);`). The Receiver field carries the bound
            // callee so Codegen can emit `CallIndirect`. See closure.md §6.
            var varType = env.LookupVar(funcId.Name);
            if (varType is Z42FuncType vft)
            {
                CheckArgCount(freeArgs.Count, vft.MinArgCount, vft.Params.Count, call.Span);
                CheckArgTypes(call.Args, freeArgs, vft.Params);
                CheckArgModifiers(call.Args, freeArgs, vft, env, call.Span);

                // 2026-05-02 impl-closure-l3-monomorphize: 若该 var 是已知函数的 alias
                //（`var f = Helper; f();`），直接 emit 静态 Call 而非 CallIndirect。
                //  与"funcId 直接是顶层函数名"路径走相同 IR shape。
                var aliasFq = env.LookupAlias(funcId.Name);
                if (aliasFq != null)
                {
                    return new BoundCall(BoundCallKind.Free, null, null, null,
                        aliasFq, freeArgs, vft.Ret, call.Span);
                }

                var calleeBound = new BoundIdent(funcId.Name, vft, funcId.Span);
                return new BoundCall(BoundCallKind.Free, calleeBound, null, null,
                    null, freeArgs, vft.Ret, call.Span);
            }
            // Unknown function — report error
            BindIdent(funcId, env);
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"undefined function or variable `{funcId.Name}`", funcId.Span);
            return new BoundCall(BoundCallKind.Free, null, null, null,
                funcId.Name, freeArgs, Z42Type.Error, call.Span);
        }

        // Non-identifier callee (rare): indirect call via expression — see closure.md §6.
        var calleeExpr = BindExpr(call.Callee, env);
        if (calleeExpr.Type is Z42FuncType ft2)
        {
            CheckArgCount(freeArgs.Count, ft2.MinArgCount, ft2.Params.Count, call.Span);
            CheckArgTypes(call.Args, freeArgs, ft2.Params);
            CheckArgModifiers(call.Args, freeArgs, ft2, env, call.Span);
            return new BoundCall(BoundCallKind.Free, calleeExpr, null, null,
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

    /// L3-G4h step3: List/Dictionary 已迁移到源码类；2026-04-26 script-first-stringbuilder
    /// 把 StringBuilder 也迁到源码类。仅 Array 保留 pseudo 路径
    /// (Array 是 VM 原生 Value variant，不是 z42 class)。
    private bool IsBuiltinCollectionType(string typeName)
    {
        return typeName == "Array";
    }

    private void CheckArgCount(int actual, int min, int max, Span span)
    {
        if (actual < min || actual > max)
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"expected {min}–{max} argument(s), got {actual}", span);
    }

    // ── Parameter modifiers (spec: define-ref-out-in-parameters) ──────────────

    /// Bind a callsite `ref` / `out` / `in` argument. For `out var x`, declare
    /// the local in the caller's scope with placeholder `Unknown` type; the
    /// matched parameter's type patches it in `CheckArgTypes`.
    private BoundExpr BindModifiedArg(ModifiedArg ma, TypeEnv env)
    {
        BoundOutVarDecl? boundDecl = null;
        if (ma.Modifier == ArgModifier.Out && ma.OutDecl is { } decl)
        {
            // Placeholder Unknown — patched once signature is known.
            env.Define(decl.Name, Z42Type.Unknown);
            boundDecl = new BoundOutVarDecl(decl.Name, Z42Type.Unknown, decl.Span);
        }

        var inner = BindExpr(ma.Inner, env);
        return new BoundModifiedArg(inner, ma.Modifier, boundDecl, inner.Type, ma.Span);
    }

    /// Validate that `ref` / `out` / `in` callsite usage matches the callee's
    /// parameter modifiers, that argument positions tagged as ref/out/in carry
    /// lvalue inner expressions, and that types match exactly across the
    /// modifier boundary (no implicit conversion). Patches `out var x` local
    /// types from matching parameter types.
    private void CheckArgModifiers(
        IReadOnlyList<Expr>      origArgs,
        IReadOnlyList<BoundExpr> boundArgs,
        Z42FuncType?             funcType,
        TypeEnv                  env,
        Span                     callSpan)
    {
        int n = boundArgs.Count;
        for (int i = 0; i < n; i++)
        {
            var origIsModified = origArgs[i] is ModifiedArg;
            var boundIsModified = boundArgs[i] is BoundModifiedArg;
            var paramMod = funcType?.ModifierAt(i) ?? ParamModifier.None;

            // Sanity: bound and orig should agree on modifier presence.
            // (Defensive — should never desync given BindModifiedArg path.)
            if (origIsModified != boundIsModified) continue;

            // Case 1: signature has modifier, callsite missing — error.
            if (paramMod != ParamModifier.None && !origIsModified)
            {
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"argument {i + 1}: parameter has `{ModifierKeyword(paramMod)}` modifier; " +
                    $"call site must use `{ModifierKeyword(paramMod)}`",
                    origArgs[i].Span);
                continue;
            }

            // Case 2: callsite has modifier, signature missing — error.
            if (paramMod == ParamModifier.None && origIsModified)
            {
                var ma = (ModifiedArg)origArgs[i];
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"argument {i + 1}: parameter has no modifier; remove `{ArgModifierKeyword(ma.Modifier)}`",
                    ma.Span);
                continue;
            }

            // Case 3: both have modifiers — must match exactly.
            if (origIsModified)
            {
                var ma = (ModifiedArg)origArgs[i];
                var bma = (BoundModifiedArg)boundArgs[i];
                if (!ModifiersMatch(ma.Modifier, paramMod))
                {
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"argument {i + 1}: expected `{ModifierKeyword(paramMod)}`, got `{ArgModifierKeyword(ma.Modifier)}`",
                        ma.Span);
                    continue;
                }

                // Lvalue requirement: ref / out / in inner must be a writable
                // location reference (variable, array element, or object field).
                // For `out var x`, the inline declaration is by definition lvalue.
                if (ma.OutDecl is null && !IsLvalueForRef(bma.Inner))
                {
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"argument {i + 1}: `{ArgModifierKeyword(ma.Modifier)}` requires an lvalue (variable, field, or array element)",
                        ma.Span);
                    continue;
                }

                // Patch `out var x` local type from the matched parameter type.
                if (ma.OutDecl is { } d && i < (funcType?.Params.Count ?? 0))
                {
                    var pType = funcType!.Params[i];
                    env.Define(d.Name, pType);
                }
            }
        }
    }

    private static bool ModifiersMatch(ArgModifier arg, ParamModifier param)
    {
        return (arg, param) switch
        {
            (ArgModifier.Ref, ParamModifier.Ref) => true,
            (ArgModifier.Out, ParamModifier.Out) => true,
            (ArgModifier.In,  ParamModifier.In)  => true,
            (ArgModifier.None, ParamModifier.None) => true,
            _ => false,
        };
    }

    private static string ModifierKeyword(ParamModifier m) => m switch
    {
        ParamModifier.Ref => "ref",
        ParamModifier.Out => "out",
        ParamModifier.In  => "in",
        _ => "<none>",
    };

    private static string ArgModifierKeyword(ArgModifier m) => m switch
    {
        ArgModifier.Ref => "ref",
        ArgModifier.Out => "out",
        ArgModifier.In  => "in",
        _ => "<none>",
    };

    /// Test whether a bound expression is a writable location reference for
    /// the purposes of `ref` / `out` / `in` arguments. Allowed:
    ///   - `BoundIdent` resolving to a local variable (or `out var x` decl)
    ///   - `BoundIndex` (`a[i]` — array / list element)
    ///   - `BoundMember` (`obj.field` — object field; not a method/property)
    ///
    /// Disallowed: literals, function calls, arithmetic, casts, `this`-only
    /// receivers without member access.
    internal static bool IsLvalueForRef(BoundExpr expr) => expr switch
    {
        BoundIdent       => true,
        BoundIndex       => true,
        BoundMember      => true,
        _                => false,
    };

    /// Modifier-aware overload lookup (spec: define-ref-out-in-parameters,
    /// Decision 7). Tries keys in order:
    ///   1. `Name$Arity$<argModSig>` — modifier-tagged key (same-arity overload)
    ///   2. `Name`                    — bare (single method, no overload)
    ///   3. `Name$Arity`              — legacy arity-only key (no modifier collision)
    /// Returns matching sig + the resolved key for IR codegen, or (null, null).
    internal static (Z42FuncType? Sig, string? Key) LookupMethodOverload(
        IReadOnlyDictionary<string, Z42FuncType> table,
        string memberName,
        IReadOnlyList<Expr> args)
    {
        var argModSig = ModifierMangling.PatternFromArgs(args);
        if (!string.IsNullOrEmpty(argModSig))
        {
            var modKey = $"{memberName}${args.Count}${argModSig}";
            if (table.TryGetValue(modKey, out var modSigVal))
                return (modSigVal, modKey);
        }
        if (table.TryGetValue(memberName, out var bare)) return (bare, memberName);
        var arityKey = $"{memberName}${args.Count}";
        if (table.TryGetValue(arityKey, out var ar)) return (ar, arityKey);
        return (null, null);
    }

    private void CheckArgTypes(
        IReadOnlyList<Expr>      origArgs,
        IReadOnlyList<BoundExpr> boundArgs,
        IReadOnlyList<Z42Type>   paramTypes)
    {
        int n = Math.Min(boundArgs.Count, paramTypes.Count);
        for (int i = 0; i < n; i++)
        {
            // For modifier-bearing arguments (ref/out/in), require exact type
            // match (no implicit conversion across the reference boundary).
            // For `out var x`, the local was declared as Unknown and CheckArgModifiers
            // patches it from paramType — skip strict check here.
            if (boundArgs[i] is BoundModifiedArg bma)
            {
                if (bma.OutDecl is not null) continue;
                if (!paramTypes[i].Equals(bma.Inner.Type)
                    && bma.Inner.Type is not Z42ErrorType
                    && bma.Inner.Type is not Z42UnknownType
                    && paramTypes[i] is not Z42UnknownType)
                {
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"argument {i + 1}: `{ArgModifierKeyword(bma.Modifier)}` requires exact type match — expected `{paramTypes[i]}`, got `{bma.Inner.Type}`",
                        origArgs[i].Span);
                }
                continue;
            }
            RequireAssignable(paramTypes[i], boundArgs[i].Type, origArgs[i].Span,
                $"argument {i + 1}: expected `{paramTypes[i]}`, got `{boundArgs[i].Type}`");
        }
    }

    /// Uppercase first character ("string" → "String") for mapping a primitive
    /// type name to its stdlib class name (`struct int` / `class String` etc).
    private static string CapitalizeFirst(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// L3-G2: infer type args for a generic function from its argument types,
    /// then validate each satisfies the corresponding `where` constraint.
    /// Inference algorithm (single-level, sufficient for L3-G2):
    ///   for each parameter i whose signature type is `Z42GenericParamType(T)`,
    ///   bind T to `boundArgs[i].Type` (first binding wins; conflicts ignored for now).
    private void InferAndValidateFuncConstraints(
        string funcName, Z42FuncType ft, IReadOnlyList<BoundExpr> boundArgs, Span callSpan)
    {
        var inferred = InferFuncTypeArgs(ft, boundArgs);
        if (inferred.Count == 0) return;
        var typeParams = inferred.Keys.ToList();
        var typeArgs   = typeParams.Select(k => inferred[k]).ToList();
        ValidateGenericConstraints(funcName, typeParams, typeArgs, _funcConstraints, callSpan);
    }

    /// Infer T → concrete-type map by scanning parameter positions of generic type T.
    /// First occurrence wins.
    private static Dictionary<string, Z42Type> InferFuncTypeArgs(
        Z42FuncType ft, IReadOnlyList<BoundExpr> boundArgs)
    {
        var inferred = new Dictionary<string, Z42Type>(StringComparer.Ordinal);
        int n = Math.Min(boundArgs.Count, ft.Params.Count);
        for (int i = 0; i < n; i++)
        {
            if (ft.Params[i] is Z42GenericParamType gp && !inferred.ContainsKey(gp.Name))
                inferred[gp.Name] = boundArgs[i].Type;
        }
        return inferred;
    }

    /// Substitute generic params in return type using args inferred from parameter positions.
    /// Returns `ft.Ret` unchanged when no substitution applies (non-generic, no match).
    private static Z42Type SubstituteGenericReturn(Z42FuncType ft, IReadOnlyList<BoundExpr> boundArgs)
    {
        if (ft.Ret is not Z42GenericParamType retGp) return ft.Ret;
        var inferred = InferFuncTypeArgs(ft, boundArgs);
        return inferred.TryGetValue(retGp.Name, out var concrete) ? concrete : ft.Ret;
    }

    /// 2026-05-05 fix-generic-extern-infer: substitute every `Z42GenericParamType`
    /// occurrence in the parameter list using inference from boundArgs. Returns
    /// the original list unchanged when nothing is inferred. Used to make
    /// CheckArgTypes accept `BlackBox<T>(int)` etc. as `int` matching `T`'s
    /// inferred slot rather than the literal type-param.
    private static IReadOnlyList<Z42Type> SubstituteGenericParams(
        Z42FuncType ft, IReadOnlyList<BoundExpr> boundArgs)
    {
        var inferred = InferFuncTypeArgs(ft, boundArgs);
        if (inferred.Count == 0) return ft.Params;
        return ft.Params.Select(p =>
            p is Z42GenericParamType gp && inferred.TryGetValue(gp.Name, out var concrete)
                ? concrete : p).ToList();
    }
}
