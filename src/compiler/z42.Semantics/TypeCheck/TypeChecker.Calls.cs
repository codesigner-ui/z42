using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Semantics.Symbols;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// Call binding and argument checking — part of the TypeChecker partial class.
public sealed partial class TypeChecker
{
    // ── Call ──────────────────────────────────────────────────────────────────

    private BoundExpr BindCall(CallExpr call, TypeEnv env)
    {
        // spec add-named-arguments: validate positional-before-named once for
        // the whole call site (Z1001). Name → param-position reorder is wired
        // per branch via ResolveArgPositions / ReorderToPositional below; the
        // common "no named args" call path takes a fast no-op.
        CheckPositionalBeforeNamed(call.Args);

        // ── Static class method: ClassName.Method(args) ──────────────────────
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var clsName }, Member: var staticMember }
            && _symbols.Classes.TryGetValue(clsName, out var staticCt))
        {
            bool isImported = _symbols.ImportedClassNames.Contains(clsName);
            // Spec define-ref-out-in-parameters (Decision 7): modifier-aware overload.
            var (staticSym, staticKey) = LookupMethodOverload(staticCt.StaticMethods, staticMember, call.Args);
            if (staticSym is not null)
            {
                var staticSig = staticSym.Signature;
                // spec add-named-arguments: reorder named args to param positions.
                // calleeParams sourced from local AST when available; null when
                // imported (named args → Z1002 fallback).
                var (staticOrigArgs, args) = BindArgsReordered(
                    call.Args, staticSym.Decl?.Params, env, call.Span);
                CheckArgCount(args.Count, staticSig.MinArgCount, staticSig.Params.Count, call.Span);
                // 2026-05-05 fix-generic-extern-infer: substitute T → concrete
                // before arg-type validation so `BlackBox<T>(int)` etc. accept
                // int args. Same path also handles non-extern generic statics.
                var checkedParams = SubstituteGenericParams(staticSig, args);
                CheckArgTypes(staticOrigArgs, args, checkedParams);
                CheckArgModifiers(staticOrigArgs, args, staticSig, env, call.Span);
                var inferredRet = SubstituteGenericReturn(staticSig, args);
                // Use resolved key when overload (modifier-tagged or arity-tagged) so
                // IR Codegen / VM dispatch resolves to the correct variant.
                string resolvedStatic = staticKey != staticMember ? staticKey! : staticMember;
                return new BoundCall(BoundCallKind.Static, null, clsName, resolvedStatic,
                    null, args, inferredRet, call.Span, Symbol: staticSym);
            }
            // Imported class: method not found → try DepIndex, or defer to IrGen
            if (isImported)
            {
                var args = call.Args.Select(a => BindArgValue(a, env)).ToList();
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
                var args = call.Args.Select(a => BindArgValue(a, env)).ToList();

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
            var argBound = call.Args.Select(a => BindArgValue(a, env)).ToList();

            // L3-G4a: method call on instantiated generic class — substitute type params in sig.
            if (recvExpr.Type is Z42InstantiatedType inst)
            {
                var subMap = BuildSubstitutionMap(inst);
                var def    = inst.Definition;
                var instArityKey = $"{mCallee.Member}${argBound.Count}";
                bool byBare  = def.Methods.TryGetValue(mCallee.Member, out var mt1);
                IMethodSymbol? mt2 = null;
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
                    var mtSub = (Z42FuncType)SubstituteTypeParams(mtRaw.Signature, subMap);
                    // spec add-named-arguments Part 2: re-bind with reorder if any named arg.
                    List<Argument> instOrigArgs = call.Args.ToList();
                    if (call.Args.Any(a => a.Name is not null))
                        (instOrigArgs, argBound) = BindArgsReordered(call.Args, mtRaw.Decl?.Params, env, call.Span);
                    CheckArgCount(argBound.Count, mtSub.MinArgCount, mtSub.Params.Count, call.Span);
                    CheckArgTypes(instOrigArgs, argBound, mtSub.Params);
                    CheckArgModifiers(instOrigArgs, argBound, mtSub, env, call.Span);
                    bool isVirtual = _symbols.VirtualMethods.TryGetValue(def.Name, out var vmSet)
                        && (vmSet.Contains(mCallee.Member) || vmSet.Contains(instArityKey));
                    return new BoundCall(
                        isVirtual ? BoundCallKind.Virtual : BoundCallKind.Instance,
                        recvExpr, def.Name, resolvedMethodName, null, argBound, mtSub.Ret, call.Span,
                        Symbol: mtRaw);
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
                var (mtSym, instKey) = LookupMethodOverload(ct.Methods, mCallee.Member, call.Args);
                // Resolved method name for IR codegen — use the actual key found.
                string resolvedMethodName = instKey ?? mCallee.Member;
                bool isImportedCls = _symbols.ImportedClassNames.Contains(ct.Name);
                if (mtSym is not null)
                {
                    var mt = mtSym.Signature;
                    bool insideClass = env.CurrentClass == ct.Name;
                    var visKey = ct.MemberVisibility.ContainsKey(mCallee.Member)
                        ? mCallee.Member : instArityKey;
                    if (!insideClass
                        && ct.MemberVisibility.TryGetValue(visKey, out var mv)
                        && mv == Visibility.Private)
                        _diags.Error(DiagnosticCodes.AccessViolation,
                            $"method `{mCallee.Member}` is private to `{ct.Name}`", call.Span);
                    // spec add-named-arguments Part 2: re-bind with reorder if any named arg.
                    List<Argument> ctOrigArgs = call.Args.ToList();
                    if (call.Args.Any(a => a.Name is not null))
                        (ctOrigArgs, argBound) = BindArgsReordered(call.Args, mtSym.Decl?.Params, env, call.Span);
                    CheckArgCount(argBound.Count, mt.MinArgCount, mt.Params.Count, call.Span);
                    CheckArgTypes(ctOrigArgs, argBound, mt.Params);
                    CheckArgModifiers(ctOrigArgs, argBound, mt, env, call.Span);
                    // Imported classes always use Instance method calls (no virtual dispatch in stdlib).
                    // User-defined classes may use Virtual dispatch.
                    bool isVirtual = !isImportedCls
                        && _symbols.VirtualMethods.TryGetValue(ct.Name, out var vmSet)
                        && (vmSet.Contains(mCallee.Member) || vmSet.Contains(instArityKey));
                    return new BoundCall(
                        isVirtual ? BoundCallKind.Virtual : BoundCallKind.Instance,
                        recvExpr, ct.Name, resolvedMethodName, null, argBound, mt.Ret, call.Span,
                        Symbol: mtSym);
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
                if (ifaceType.Methods.TryGetValue(mCallee.Member, out var imSym))
                {
                    var imt = imSym.Signature;
                    // Substitute generic interface T → concrete TypeArgs for method
                    // dispatch. `IEquatable<int>.Equals(T)` → `Equals(int)` etc.
                    var subMap = BuildInterfaceSubstitutionMap(ifaceType);
                    var imtSub = subMap is null ? imt
                                                : (Z42FuncType)SubstituteTypeParams(imt, subMap);
                    // spec add-named-arguments Part 2: re-bind with reorder if any named arg.
                    List<Argument> ifaceOrigArgs = call.Args.ToList();
                    if (call.Args.Any(a => a.Name is not null))
                        (ifaceOrigArgs, argBound) = BindArgsReordered(call.Args, imSym.Decl?.Params, env, call.Span);
                    CheckArgCount(argBound.Count, imtSub.MinArgCount, imtSub.Params.Count, call.Span);
                    CheckArgTypes(ifaceOrigArgs, argBound, imtSub.Params);
                    CheckArgModifiers(ifaceOrigArgs, argBound, imtSub, env, call.Span);
                    return new BoundCall(BoundCallKind.Virtual, recvExpr, ifaceType.Name,
                        mCallee.Member, null, argBound, imtSub.Ret, call.Span,
                        Symbol: imSym);
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
                    && bc.Methods.TryGetValue(mCallee.Member, out var bcMtSym))
                {
                    var bcMt = bcMtSym.Signature;
                    // spec add-named-arguments Part 2: re-bind with reorder if any named arg.
                    List<Argument> bcOrigArgs = call.Args.ToList();
                    if (call.Args.Any(a => a.Name is not null))
                        (bcOrigArgs, argBound) = BindArgsReordered(call.Args, bcMtSym.Decl?.Params, env, call.Span);
                    CheckArgCount(argBound.Count, bcMt.MinArgCount, bcMt.Params.Count, call.Span);
                    CheckArgTypes(bcOrigArgs, argBound, bcMt.Params);
                    CheckArgModifiers(bcOrigArgs, argBound, bcMt, env, call.Span);
                    return new BoundCall(BoundCallKind.Virtual, recvExpr, bc.Name,
                        mCallee.Member, null, argBound, bcMt.Ret, call.Span,
                        Symbol: bcMtSym);
                }
                foreach (var iface in bundle.Interfaces)
                {
                    if (iface.Methods.TryGetValue(mCallee.Member, out var gmtSym))
                    {
                        var gmt = gmtSym.Signature;
                        // spec add-named-arguments Part 2: re-bind with reorder if any named arg.
                        List<Argument> gpOrigArgs = call.Args.ToList();
                        if (call.Args.Any(a => a.Name is not null))
                            (gpOrigArgs, argBound) = BindArgsReordered(call.Args, gmtSym.Decl?.Params, env, call.Span);
                        CheckArgCount(argBound.Count, gmt.MinArgCount, gmt.Params.Count, call.Span);
                        CheckArgTypes(gpOrigArgs, argBound, gmt.Params);
                        CheckArgModifiers(gpOrigArgs, argBound, gmt, env, call.Span);
                        return new BoundCall(BoundCallKind.Virtual, recvExpr, iface.Name,
                            mCallee.Member, null, argBound, gmt.Ret, call.Span,
                            Symbol: gmtSym);
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
                IMethodSymbol? pmt2 = null;
                bool primArity = !primBare && primClsType.Methods.TryGetValue(primArityKey, out pmt2);
                var pmtSym = primBare ? pmt1 : primArity ? pmt2 : null;
                if (pmtSym is not null)
                {
                    var pmt = pmtSym.Signature;
                    string resolved = primArity ? primArityKey : mCallee.Member;
                    // spec add-named-arguments Part 2: re-bind with reorder if any named arg.
                    List<Argument> pmtOrigArgs = call.Args.ToList();
                    if (call.Args.Any(a => a.Name is not null))
                        (pmtOrigArgs, argBound) = BindArgsReordered(call.Args, pmtSym.Decl?.Params, env, call.Span);
                    CheckArgCount(argBound.Count, pmt.MinArgCount, pmt.Params.Count, call.Span);
                    CheckArgTypes(pmtOrigArgs, argBound, pmt.Params);
                    CheckArgModifiers(pmtOrigArgs, argBound, pmt, env, call.Span);
                    return new BoundCall(BoundCallKind.Instance, recvExpr, primitiveClassName,
                        resolved, null, argBound, pmt.Ret, call.Span,
                        Symbol: pmtSym);
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
        var freeArgs = call.Args.Select(a => BindArgValue(a, env)).ToList();

        // Bare name inside current class's static methods
        if (call.Callee is IdentExpr { Name: var bareCallName }
            && env.CurrentClass != null
            && _symbols.Classes.TryGetValue(env.CurrentClass, out var curCt)
            && curCt.StaticMethods.TryGetValue(bareCallName, out var bareSym))
        {
            var bareSig = bareSym.Signature;
            // spec add-named-arguments Part 2: re-bind with reorder if any named arg.
            List<Argument> bareOrigArgs = call.Args.ToList();
            if (call.Args.Any(a => a.Name is not null))
                (bareOrigArgs, freeArgs) = BindArgsReordered(call.Args, bareSym.Decl?.Params, env, call.Span);
            CheckArgCount(freeArgs.Count, bareSig.MinArgCount, bareSig.Params.Count, call.Span);
            CheckArgTypes(bareOrigArgs, freeArgs, bareSig.Params);
            CheckArgModifiers(bareOrigArgs, freeArgs, bareSig, env, call.Span);
            return new BoundCall(BoundCallKind.Static, null, env.CurrentClass, bareCallName,
                null, freeArgs, bareSig.Ret, call.Span,
                Symbol: bareSym);
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
                // spec add-named-arguments Part 2: re-bind with reorder when any
                // named arg. Decl pulled from SymbolTable.FuncDecls (populated
                // for locally collected top-level funcs). Local nested funcs
                // and imported funcs have no entry → Z1002 fallback inside helper.
                List<Argument> ftOrigArgs = call.Args.ToList();
                if (call.Args.Any(a => a.Name is not null))
                {
                    _symbols.FuncDecls.TryGetValue(resolvedFuncName, out var fnDecl);
                    (ftOrigArgs, freeArgs) = BindArgsReordered(
                        call.Args, fnDecl?.Params, env, call.Span);
                }
                CheckArgCount(freeArgs.Count, ft.MinArgCount, ft.Params.Count, call.Span);
                CheckArgTypes(ftOrigArgs, freeArgs, ft.Params);
                CheckArgModifiers(ftOrigArgs, freeArgs, ft, env, call.Span);
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

                // split-symbol-from-type Phase 4: function-value variable call →
                // BoundIndirectCall (not BoundCall — there's no method symbol).
                var calleeBound = new BoundIdent(funcId.Name, vft, funcId.Span);
                return new BoundIndirectCall(calleeBound, freeArgs, vft.Ret, call.Span);
            }
            // add-generic-func-constraint (2026-05-11): variable of generic-param type T
            // whose effective constraints include FuncSignature is callable. Treat as
            // Z42FuncType with the constraint signature; emit BoundIndirectCall (same path
            // as lambda values).
            if (varType is Z42GenericParamType gpVar
                && _symbols.LookupActiveTypeParamConstraints(gpVar.Name).FuncSignature is { } gpSig)
            {
                CheckArgCount(freeArgs.Count, gpSig.MinArgCount, gpSig.Params.Count, call.Span);
                CheckArgTypes(call.Args, freeArgs, gpSig.Params);
                CheckArgModifiers(call.Args, freeArgs, gpSig, env, call.Span);
                var calleeBound = new BoundIdent(funcId.Name, gpSig, funcId.Span);
                return new BoundIndirectCall(calleeBound, freeArgs, gpSig.Ret, call.Span);
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
            // split-symbol-from-type Phase 4: arbitrary expression call →
            // BoundIndirectCall.
            return new BoundIndirectCall(calleeExpr, freeArgs, ft2.Ret, call.Span);
        }
        _diags.Error(DiagnosticCodes.TypeMismatch,
            $"cannot call non-function type `{calleeExpr.Type}`", call.Callee.Span);
        return new BoundCall(BoundCallKind.Free, null, null, null,
            null, freeArgs, Z42Type.Error, call.Span);
    }
}
