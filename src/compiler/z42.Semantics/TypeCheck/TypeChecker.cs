using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Syntax.Lexer;
using Z42.Core.Diagnostics;
using Z42.Syntax.Parser;
using Z42.IR;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Phase 1 type checker — orchestrator + body binder.
///
/// Three-phase design:
///   Pass 0  — <see cref="SymbolCollector"/>: collect shapes → <see cref="SymbolTable"/>
///   Pass 1  — TypeChecker (this class): bind function bodies using SymbolTable
///   Pass 2  — <see cref="FlowAnalyzer"/>: reachability + definite assignment
///
/// Body binding is split across partial class files:
/// • TypeChecker.cs       — orchestration, body entry points, helpers
/// • TypeChecker.Stmts.cs — statement binding
/// • TypeChecker.Exprs.cs — expression type inference
/// </summary>
public sealed partial class TypeChecker : ITypeInferrer
{
    private readonly DiagnosticBag   _diags;
    private readonly LanguageFeatures _features;
    private readonly DependencyIndex? _depIndex;

    // ── Readonly symbol table (populated by SymbolCollector, consumed here) ───
    private SymbolTable _symbols = null!;

    // ── Binding outputs ──────────────────────────────────────────────────────
    private readonly Dictionary<FunctionDecl, BoundBlock> _boundBodies = new();
    private readonly Dictionary<Param, BoundExpr> _boundDefaults =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FieldDecl, BoundExpr> _boundStaticInits =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionDecl, IReadOnlyList<BoundExpr>> _boundBaseCtorArgs = new();

    /// Resolved generic constraints keyed by declaration name. (L3-G2, L3-G2.5)
    /// Populated in Pass 0.5 after SymbolCollector; consumed at body binding and call sites.
    private readonly Dictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>>
        _funcConstraints = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>>
        _classConstraints = new();

    public TypeChecker(DiagnosticBag diags, LanguageFeatures? features = null, DependencyIndex? depIndex = null)
    {
        _diags    = diags;
        _features = features ?? LanguageFeatures.Phase1;
        _depIndex = depIndex;
    }

    /// The frozen symbol table, available after Check() begins Pass 1.
    internal SymbolTable Symbols => _symbols;

    // ── Public entry points ─────────────────────────────────────────────────

    /// Convenience: runs all 3 phases (Pass 0 + Pass 1 + Pass 2) in one call.
    public SemanticModel Check(CompilationUnit cu, ImportedSymbols? imported = null)
    {
        var binder  = new SymbolCollector(_diags);
        var symbols = binder.Collect(cu, imported);
        // L3-G3d: rehydrate imported `where` constraints so ValidateGenericConstraints
        // fires for `new ImportedGeneric<T>()` / imported generic func calls.
        _imported = imported;
        return Infer(cu, symbols);
    }

    private ImportedSymbols? _imported;

    /// ITypeInferrer: Pass 1 only — bind bodies using an already-collected SymbolTable.
    public SemanticModel Infer(CompilationUnit cu, SymbolTable symbols)
    {
        _symbols = symbols;

        // ── Pass 0.5: resolve generic constraints from `where` clauses (L3-G2) ──
        ResolveAllWhereConstraints(cu);
        // L3-G3d: merge imported `where` constraints from zpkg TSIG so call-site
        // validation covers `new ImportedGeneric<T>()` / imported generic calls.
        MergeImportedConstraints();

        // ── Pass 1: bind bodies (function-level error isolation) ────────────
        BindStaticFieldInits(cu);
        foreach (var cls  in cu.Classes)   TryBindClassMethods(cls);
        foreach (var impl in cu.Impls)     TryBindImplMethods(impl);
        foreach (var fn   in cu.Functions) TryBindFunction(fn);

        // ── Assemble SemanticModel from frozen symbols + bound results ──────
        return new SemanticModel(
            _symbols.Classes, _symbols.Functions, _symbols.Interfaces,
            _symbols.EnumConstants, _symbols.EnumTypes,
            _boundBodies, _boundDefaults, _boundStaticInits, _boundBaseCtorArgs,
            _symbols.ImportedClassNamespaces as Dictionary<string, string>
                ?? new Dictionary<string, string>(_symbols.ImportedClassNamespaces),
            funcConstraints:  _funcConstraints,
            classConstraints: _classConstraints,
            importedClassNames: _symbols.ImportedClassNames as IReadOnlySet<string>
                                ?? new HashSet<string>(_symbols.ImportedClassNames),
            classInterfaces: _symbols.ClassInterfaces);
    }

    // ── Body binding entry points (error-isolated) ──────────────────────────

    private void TryBindClassMethods(ClassDecl cls)
    {
        try { BindClassMethods(cls); }
        catch (CompilationException) { /* diagnostics already reported */ }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _diags.Error(DiagnosticCodes.InternalCompilerError,
                $"ICE while checking class `{cls.Name}`: [{ex.GetType().Name}] {ex.Message}", cls.Span);
        }
    }

    private void TryBindFunction(FunctionDecl fn)
    {
        try { BindFunction(fn); }
        catch (CompilationException) { /* diagnostics already reported */ }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _diags.Error(DiagnosticCodes.InternalCompilerError,
                $"ICE while checking function `{fn.Name}`: [{ex.GetType().Name}] {ex.Message}", fn.Span);
        }
    }

    /// L3 extern impl (Change 1): bind method bodies inside `impl Trait for Target { ... }`.
    /// Reuses class-method binding environment: `this` = target class; fields + type params in scope.
    private void TryBindImplMethods(ImplDecl impl)
    {
        try { BindImplMethods(impl); }
        catch (CompilationException) { /* diagnostics already reported */ }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _diags.Error(DiagnosticCodes.InternalCompilerError,
                $"ICE while checking impl block: [{ex.GetType().Name}] {ex.Message}", impl.Span);
        }
    }

    private void BindImplMethods(ImplDecl impl)
    {
        if (impl.TargetType is not Syntax.Parser.NamedType targetNt) return;
        if (!_symbols.Classes.TryGetValue(targetNt.Name, out var targetClass)) return;
        var targetTypeParams = targetClass.TypeParams;
        if (targetTypeParams is { Count: > 0 })
            _symbols.PushTypeParams(targetTypeParams, _classConstraints.GetValueOrDefault(targetNt.Name));
        try
        {
            foreach (var method in impl.Methods)
            {
                if (ValidateNativeMethod(method, isInstance: !method.IsStatic)) continue;
                var env   = new TypeEnv(_symbols.Functions, _symbols.Classes, _symbols.ImportedClassNames);
                var scope = env.WithClass(targetNt.Name);
                if (!method.IsStatic)
                {
                    scope.Define("this", targetClass);
                    foreach (var (fname, ftype) in targetClass.Fields)
                        scope.Define(fname, ftype);
                }
                CheckParamNames(method.Params);
                foreach (var p in method.Params)
                    scope.Define(p.Name, _symbols.ResolveType(p.Type));
                BindParamDefaults(method.Params, scope);
                var retType = _symbols.ResolveType(method.ReturnType);
                _boundBodies[method] = BindBlock(method.Body, scope, retType);
                if (!method.IsAbstract)
                {
                    if (retType is not Z42VoidType and not Z42UnknownType and not Z42ErrorType
                        && !FlowAnalyzer.AlwaysReturns(_boundBodies[method]))
                        _diags.Error(DiagnosticCodes.MissingReturn,
                            $"not all code paths return a value in `{method.Name}`", method.Span);
                    FlowAnalyzer.CheckDefiniteAssignment(_boundBodies[method], _diags);
                }
            }
        }
        finally
        {
            if (targetTypeParams is { Count: > 0 }) _symbols.PopTypeParams();
        }
    }

    private void BindClassMethods(ClassDecl cls)
    {
        if (!_symbols.Classes.TryGetValue(cls.Name, out var classType)) return;
        if (cls.TypeParams != null)
            _symbols.PushTypeParams(cls.TypeParams, _classConstraints.GetValueOrDefault(cls.Name));
        try
        {
        foreach (var method in cls.Methods)
        {
            if (ValidateNativeMethod(method, isInstance: !method.IsStatic)) continue;
            var env   = new TypeEnv(_symbols.Functions, _symbols.Classes, _symbols.ImportedClassNames);
            var scope = env.WithClass(cls.Name);
            if (!method.IsStatic)
            {
                scope.Define("this", classType);
                foreach (var (fname, ftype) in classType.Fields)
                    scope.Define(fname, ftype);
            }
            CheckParamNames(method.Params);
            foreach (var p in method.Params)
                scope.Define(p.Name, _symbols.ResolveType(p.Type));
            BindParamDefaults(method.Params, scope);
            bool isCtor = method.Name == cls.Name;
            if (isCtor && method.BaseCtorArgs is { } baseCtorArgs)
                _boundBaseCtorArgs[method] = baseCtorArgs.Select(a => BindExpr(a, scope)).ToList();
            _boundBodies[method] = BindBlock(method.Body, scope,
                isCtor ? Z42Type.Void : _symbols.ResolveType(method.ReturnType));
            if (!method.IsAbstract)
            {
                var methodRetType = isCtor ? Z42Type.Void : _symbols.ResolveType(method.ReturnType);
                if (methodRetType is not Z42VoidType and not Z42UnknownType and not Z42ErrorType
                    && !FlowAnalyzer.AlwaysReturns(_boundBodies[method]))
                    _diags.Error(DiagnosticCodes.MissingReturn,
                        $"not all code paths return a value in `{method.Name}`", method.Span);
                FlowAnalyzer.CheckDefiniteAssignment(_boundBodies[method], _diags);
            }
        }
        }
        finally { if (cls.TypeParams != null) _symbols.PopTypeParams(); }
    }

    private void BindFunction(FunctionDecl fn)
    {
        if (ValidateNativeMethod(fn, isInstance: false)) return;
        if (fn.TypeParams != null)
            _symbols.PushTypeParams(fn.TypeParams, _funcConstraints.GetValueOrDefault(fn.Name));
        try
        {
            var env   = new TypeEnv(_symbols.Functions, _symbols.Classes, _symbols.ImportedClassNames);
            var scope = env.PushScope();
            CheckParamNames(fn.Params);
            foreach (var p in fn.Params)
                scope.Define(p.Name, _symbols.ResolveType(p.Type));
            BindParamDefaults(fn.Params, scope);
            _boundBodies[fn] = BindBlock(fn.Body, scope, _symbols.ResolveType(fn.ReturnType));
            var fnRetType = _symbols.ResolveType(fn.ReturnType);
            if (fnRetType is not Z42VoidType and not Z42UnknownType and not Z42ErrorType
                && fnRetType is not Z42GenericParamType
                && !FlowAnalyzer.AlwaysReturns(_boundBodies[fn]))
                _diags.Error(DiagnosticCodes.MissingReturn,
                    $"not all code paths return a value in `{fn.Name}`", fn.Span);
            FlowAnalyzer.CheckDefiniteAssignment(_boundBodies[fn], _diags);
        }
        finally { if (fn.TypeParams != null) _symbols.PopTypeParams(); }
    }

    // ── Generic constraints (L3-G2) ─────────────────────────────────────────

    /// Validate that each type argument satisfies the constraints declared in `where`
    /// on the owning decl. `declName` keys into the constraint map; `typeParams` and
    /// `typeArgs` must have matching counts. Reports `TypeMismatch` per unmet constraint.
    internal void ValidateGenericConstraints(
        string declName,
        IReadOnlyList<string> typeParams,
        IReadOnlyList<Z42Type> typeArgs,
        Dictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>> constraintMap,
        Span callSpan)
    {
        if (!constraintMap.TryGetValue(declName, out var constraints)) return;
        // Build a name→typeArg map once for bare-typeparam constraint checks.
        var typeArgByName = new Dictionary<string, Z42Type>(StringComparer.Ordinal);
        for (int i = 0; i < typeParams.Count && i < typeArgs.Count; i++)
            typeArgByName[typeParams[i]] = typeArgs[i];

        for (int i = 0; i < typeParams.Count && i < typeArgs.Count; i++)
        {
            if (!constraints.TryGetValue(typeParams[i], out var bundle)) continue;
            var typeArg = typeArgs[i];
            if (bundle.BaseClass is { } baseClass
                && !TypeSatisfiesClassConstraint(typeArg, baseClass))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `{baseClass.Name}` on `{declName}`",
                    callSpan);
            foreach (var iface in bundle.Interfaces)
            {
                // L3-G2.5 chain: substitute cross-param type-args before checking
                // (e.g. `T: IEquatable<U>` with U=string → verify `T` satisfies `IEquatable<string>`).
                var substitutedIface = SubstituteInterfaceTypeArgs(iface, typeArgByName);
                if (!TypeSatisfiesInterface(typeArg, substitutedIface))
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `{substitutedIface}` on `{declName}`",
                        callSpan);
            }
            if (bundle.RequiresClass && !IsClassArg(typeArg))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `class` on `{declName}`",
                    callSpan);
            if (bundle.RequiresStruct && !IsStructArg(typeArg))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `struct` on `{declName}`",
                    callSpan);
            // L3-G2.5 ctor: `where T: new()` — type arg must have a no-arg constructor.
            if (bundle.RequiresConstructor && !HasNoArgConstructor(typeArg))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `new()` on `{declName}`",
                    callSpan);
            // L3-G2.5 enum: `where T: enum` — type arg must be an enum type.
            if (bundle.RequiresEnum && !IsEnumArg(typeArg))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `enum` on `{declName}`",
                    callSpan);
            // L3-G2.5 bare-typeparam: `where U: T` — typeArg[U] must be subtype of typeArg[T].
            if (bundle.TypeParamConstraint is { } otherTp
                && typeArgByName.TryGetValue(otherTp, out var otherArg)
                && !TypeArgSubsumedBy(typeArg, otherArg))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` is not a subtype of `{otherArg}` (required by `{typeParams[i]}: {otherTp}` on `{declName}`)",
                    callSpan);
        }
    }

    /// L3-G2.5 bare-typeparam: is `sub` a subtype of `sup`?
    /// - Same type → true; Z42ClassType via IsSubclassOf; error/unknown don't cascade.
    /// - Non-class types only satisfy via equality (primitives / interfaces / arrays).
    private bool TypeArgSubsumedBy(Z42Type sub, Z42Type sup)
    {
        if (sub == sup) return true;
        if (sub is Z42ErrorType or Z42UnknownType) return true;
        if (sup is Z42ErrorType or Z42UnknownType) return true;
        if (sub is Z42ClassType cs && sup is Z42ClassType cp)
            return cs.Name == cp.Name || _symbols.IsSubclassOf(cs.Name, cp.Name);
        return false;
    }

    /// L3-G2.5 ctor: does `typeArg` satisfy `where T: new()`?
    /// - Class: has a 0-arg constructor (ctor name matches class, with 0 params, bare or `$0`).
    /// - Struct / primitive: always OK (default-constructible).
    /// - Generic param: propagate only if T itself has the RequiresConstructor constraint.
    /// - Interface / array / option / func: rejected.
    private bool HasNoArgConstructor(Z42Type t)
    {
        switch (t)
        {
            case Z42ErrorType:
            case Z42UnknownType:
                return true; // don't cascade
            case Z42PrimType:
                return true; // default-constructible
            case Z42ClassType ct:
                if (ct.IsStruct) return true;
                return ClassHasNoArgCtor(ct.Name);
            case Z42InstantiatedType inst:
                if (inst.Definition.IsStruct) return true;
                return ClassHasNoArgCtor(inst.Definition.Name);
            case Z42GenericParamType gp:
                // If T carries `new()` transitively (via active where scope), accept.
                return _symbols.LookupEffectiveConstraints(gp.Name).RequiresConstructor;
            default:
                return false; // interfaces / arrays / options / funcs
        }
    }

    private bool ClassHasNoArgCtor(string className)
    {
        if (!_symbols.Classes.TryGetValue(className, out var ct)) return false;
        // Z42 stores constructors as methods keyed by class name; overloads get $arity suffix.
        // A no-arg ctor appears as either bare name (single ctor) or `Name$0` (overloaded).
        if (ct.Methods.TryGetValue(className, out var bare) && bare.Params.Count == 0) return true;
        if (ct.Methods.TryGetValue($"{className}$0", out _)) return true;
        // Imported classes may not have constructor in Methods map — look for any method whose
        // name starts with the class name followed by empty params (TSIG stores ctors this way).
        // For now, conservative: require explicit ctor presence.
        return false;
    }

    /// L3-G2.5 refvalue: is `typeArg` a reference type (for `where T: class`)?
    private static bool IsClassArg(Z42Type t) => t switch
    {
        Z42ClassType ct               => !ct.IsStruct,
        Z42ErrorType or Z42UnknownType => true, // don't cascade
        _                             => Z42Type.IsReferenceType(t),
    };

    /// L3-G2.5 refvalue: is `typeArg` a value type (for `where T: struct`)?
    private bool IsStructArg(Z42Type t) => t switch
    {
        Z42ClassType ct               => ct.IsStruct,
        Z42PrimType                   => !Z42Type.IsReferenceType(t), // int/bool/float/...
        Z42EnumType                   => true,                        // enum is a value type
        Z42GenericParamType gp        => _symbols.LookupEffectiveConstraints(gp.Name).RequiresStruct
                                         || _symbols.LookupEffectiveConstraints(gp.Name).RequiresEnum,
        Z42ErrorType or Z42UnknownType => true,
        _                             => false,
    };

    /// L3-G2.5 enum: is `typeArg` an enum type (for `where T: enum`)?
    /// - Z42EnumType: direct match.
    /// - Generic param: propagate only if T itself carries `RequiresEnum`.
    /// - Everything else (class / struct / primitive / interface / array): rejected.
    private bool IsEnumArg(Z42Type t) => t switch
    {
        Z42EnumType                   => true,
        Z42GenericParamType gp        => _symbols.LookupEffectiveConstraints(gp.Name).RequiresEnum,
        Z42ErrorType or Z42UnknownType => true, // don't cascade
        _                             => false,
    };

    /// Does `typeArg` satisfy the interface constraint `iface`?
    /// - Class type: via SymbolTable.ImplementsInterface (walks hierarchy).
    /// - Interface type: same-name match (interface extending not tracked yet — L3-G3).
    /// - Generic param: accept if one of its own constraints is this interface (propagate).
    /// - Primitive: routed via L3-G4b `PrimitiveImplementsInterface`. L3-G2.5 chain
    ///   additionally requires `iface.TypeArgs` (if present) to equal the primitive
    ///   itself — primitives only satisfy the self-referential form (int → IEquatable<int>).
    private bool TypeSatisfiesInterface(Z42Type typeArg, Z42InterfaceType iface) => typeArg switch
    {
        Z42ClassType ct          => ClassSatisfiesInterface(ct.Name, null, iface),
        Z42InstantiatedType inst => ClassSatisfiesInterface(inst.Definition.Name,
                                        BuildSubstitutionMap(inst), iface),
        Z42InterfaceType it      => InterfacesEqual(it, iface),
        Z42GenericParamType g    => GenericParamSatisfies(g, iface),
        Z42PrimType pt           => PrimitiveSatisfies(pt, iface),
        Z42ErrorType             => true, // don't cascade errors
        Z42UnknownType           => true,
        _                        => false,
    };

    /// L3-G2.5 chain: class-level interface check with TypeArg matching.
    /// Walks the base-class chain; for each declared `Z42InterfaceType` with a matching
    /// name, compares TypeArgs (after substituting instantiated class type params).
    /// When the constraint has no TypeArgs, name match is sufficient.
    private bool ClassSatisfiesInterface(
        string className,
        IReadOnlyDictionary<string, Z42Type>? classSubstitution,
        Z42InterfaceType constraintIface)
    {
        foreach (var declared in _symbols.ImplementedInterfacesByName(className, constraintIface.Name))
        {
            // No args on the constraint → name match is enough (backward compat).
            if (constraintIface.TypeArgs is null || constraintIface.TypeArgs.Count == 0)
                return true;
            // Declared side missing args → be lenient (likely imported w/o args tracking).
            if (declared.TypeArgs is null || declared.TypeArgs.Count != constraintIface.TypeArgs.Count)
                return true;
            bool allMatch = true;
            for (int i = 0; i < constraintIface.TypeArgs.Count; i++)
            {
                var declaredArg = classSubstitution is null
                    ? declared.TypeArgs[i]
                    : SubstituteTypeParams(declared.TypeArgs[i], classSubstitution);
                if (!TypeArgEquals(declaredArg, constraintIface.TypeArgs[i]))
                { allMatch = false; break; }
            }
            if (allMatch) return true;
        }
        return false;
    }

    /// Full interface equality including TypeArgs (length & element-wise).
    private static bool InterfacesEqual(Z42InterfaceType a, Z42InterfaceType b)
    {
        if (a.Name != b.Name) return false;
        if (a.TypeArgs is null && b.TypeArgs is null) return true;
        if (a.TypeArgs is null || b.TypeArgs is null) return true; // lenient
        if (a.TypeArgs.Count != b.TypeArgs.Count) return false;
        for (int i = 0; i < a.TypeArgs.Count; i++)
            if (!TypeArgEquals(a.TypeArgs[i], b.TypeArgs[i])) return false;
        return true;
    }

    /// L3-G2.5 chain: equality for TypeArg comparison. Z42 records use structural
    /// equality; for class types we use name-based equality because stubs vs fully
    /// collected class instances would otherwise mismatch (same `Num` at different
    /// collection phases has different inner dictionaries).
    private static bool TypeArgEquals(Z42Type a, Z42Type b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is Z42ClassType ac && b is Z42ClassType bc) return ac.Name == bc.Name;
        if (a is Z42PrimType ap && b is Z42PrimType bp)   return ap.Name == bp.Name;
        if (a is Z42GenericParamType ag && b is Z42GenericParamType bg) return ag.Name == bg.Name;
        if (a is Z42InstantiatedType ai && b is Z42InstantiatedType bi)
        {
            if (ai.Definition.Name != bi.Definition.Name) return false;
            if (ai.TypeArgs.Count != bi.TypeArgs.Count) return false;
            for (int i = 0; i < ai.TypeArgs.Count; i++)
                if (!TypeArgEquals(ai.TypeArgs[i], bi.TypeArgs[i])) return false;
            return true;
        }
        if (a is Z42ArrayType aa && b is Z42ArrayType ba) return TypeArgEquals(aa.Element, ba.Element);
        if (a is Z42OptionType ao && b is Z42OptionType bo) return TypeArgEquals(ao.Inner, bo.Inner);
        return a.Equals(b);
    }

    /// L3-G2.5 chain: a primitive `int` satisfies `IEquatable<int>` but not
    /// `IEquatable<string>` — when the constraint's TypeArgs are present, the sole
    /// type arg must equal the primitive itself (self-referential only).
    private bool PrimitiveSatisfies(Z42PrimType pt, Z42InterfaceType iface)
    {
        if (!PrimitiveImplementsInterface(pt.Name, iface.Name)) return false;
        if (iface.TypeArgs is not { Count: > 0 } args) return true;
        return args.All(a => a is Z42PrimType p && p.Name == pt.Name);
    }

    /// L3-G2.5 chain: generic param T satisfies `I<X>` if one of T's own interface
    /// constraints is `I<X>` (name AND args match after substitution).
    private static bool GenericParamSatisfies(Z42GenericParamType g, Z42InterfaceType iface)
    {
        if (g.InterfaceConstraints is null) return false;
        foreach (var c in g.InterfaceConstraints)
        {
            if (c.Name != iface.Name) continue;
            if (iface.TypeArgs is null || iface.TypeArgs.Count == 0) return true;
            if (c.TypeArgs is null || c.TypeArgs.Count != iface.TypeArgs.Count) continue;
            bool argsMatch = true;
            for (int i = 0; i < c.TypeArgs.Count; i++)
                if (!TypeArgEquals(c.TypeArgs[i], iface.TypeArgs[i]))
                { argsMatch = false; break; }
            if (argsMatch) return true;
        }
        return false;
    }

    /// L3-G2.5 chain: substitute type-param references inside an interface's
    /// TypeArgs using the current call-site `typeArg` map. `T: IEquatable<U>` with
    /// U=string becomes `IEquatable<string>` before the satisfies check.
    private static Z42InterfaceType SubstituteInterfaceTypeArgs(
        Z42InterfaceType iface, IReadOnlyDictionary<string, Z42Type> typeArgByName)
    {
        if (iface.TypeArgs is not { Count: > 0 } args) return iface;
        var substituted = new List<Z42Type>(args.Count);
        foreach (var a in args)
        {
            if (a is Z42GenericParamType gp && typeArgByName.TryGetValue(gp.Name, out var concrete))
                substituted.Add(concrete);
            else
                substituted.Add(a);
        }
        return new Z42InterfaceType(iface.Name, iface.Methods, substituted);
    }

    /// L3-G4b primitive-as-struct: primitive types satisfy interfaces via stdlib
    /// `struct int : IComparable<int> { ... }` declarations (see z42.core/src/Int.z42
    /// etc.). No hardcoded table — consults the symbol-level `ClassInterfaces` registry.
    ///
    /// Size-alias primitive names (i32, i64, short, byte, ushort, uint, ulong, f32, ...) are
    /// normalized to their canonical stdlib struct name (int/long/double/float) before lookup,
    /// so `Max<i8>(...)` reuses `struct int`'s interface list.
    private bool PrimitiveImplementsInterface(string primName, string ifaceName)
    {
        string canonical = primName switch
        {
            "i8" or "i16" or "i32" or "u8" or "u16" or "u32"
            or "sbyte" or "short" or "byte" or "ushort" or "uint" => "int",
            "i64" or "u64" or "ulong"                             => "long",
            "f32"                                                  => "float",
            "f64"                                                  => "double",
            // stdlib retains legacy uppercase `class String`; map primitive `string` to it
            // until String is migrated to a `struct string` declaration.
            "string"                                               => "String",
            _                                                      => primName,
        };
        if (!_symbols.ClassInterfaces.TryGetValue(canonical, out var ifaces)) return false;
        foreach (var iface in ifaces)
            if (iface.Name == ifaceName) return true;
        return false;
    }

    /// Does `typeArg` satisfy the base-class constraint `baseClass`? (L3-G2.5)
    /// Accepts same class or any subclass; propagates through generic params that already
    /// carry a matching base-class constraint.
    private bool TypeSatisfiesClassConstraint(Z42Type typeArg, Z42ClassType baseClass) => typeArg switch
    {
        Z42ClassType ct       => ct.Name == baseClass.Name
                                 || _symbols.IsSubclassOf(ct.Name, baseClass.Name),
        Z42GenericParamType g => g.BaseClassConstraint != null
                                 && (g.BaseClassConstraint.Name == baseClass.Name
                                     || _symbols.IsSubclassOf(g.BaseClassConstraint.Name, baseClass.Name)),
        Z42ErrorType          => true,
        Z42UnknownType        => true,
        _                     => false,
    };

    /// L3-G3d: convert imported `ExportedTypeParamConstraint` lists into
    /// `GenericConstraintBundle` maps and merge into `_classConstraints` /
    /// `_funcConstraints`. Local constraints (already resolved in Pass 0.5) win.
    private void MergeImportedConstraints()
    {
        if (_imported is null) return;
        if (_imported.ClassConstraints is { } cc)
            foreach (var (declName, raw) in cc)
                if (!_classConstraints.ContainsKey(declName))
                    _classConstraints[declName] = RehydrateConstraints(raw);
        if (_imported.FuncConstraints is { } fc)
            foreach (var (declName, raw) in fc)
                if (!_funcConstraints.ContainsKey(declName))
                    _funcConstraints[declName] = RehydrateConstraints(raw);
    }

    private IReadOnlyDictionary<string, GenericConstraintBundle> RehydrateConstraints(
        List<ExportedTypeParamConstraint> raw)
    {
        var result = new Dictionary<string, GenericConstraintBundle>(raw.Count);
        foreach (var c in raw)
        {
            var ifaces = new List<Z42InterfaceType>(c.Interfaces.Count);
            foreach (var iname in c.Interfaces)
                if (_symbols.Interfaces.TryGetValue(iname, out var it))
                    ifaces.Add(it);
            Z42ClassType? baseCls = c.BaseClass != null
                && _symbols.Classes.TryGetValue(c.BaseClass, out var bc) ? bc : null;
            result[c.TypeParam] = new GenericConstraintBundle(
                baseCls, ifaces,
                c.RequiresClass, c.RequiresStruct, c.TypeParamRef,
                c.RequiresConstructor,
                c.RequiresEnum);
        }
        return result;
    }

    /// Pass 0.5: resolve every `where` clause in the CU into cached constraint maps
    /// consulted by body binding (`PushTypeParams`) and call-site validation.
    private void ResolveAllWhereConstraints(CompilationUnit cu)
    {
        foreach (var fn in cu.Functions)
        {
            if (fn.TypeParams == null) continue;
            var map = ResolveWhereConstraints(fn.Where, fn.TypeParams, fn.Span);
            if (map != null) _funcConstraints[fn.Name] = map;
        }
        foreach (var cls in cu.Classes)
        {
            if (cls.TypeParams != null)
            {
                var map = ResolveWhereConstraints(cls.Where, cls.TypeParams, cls.Span);
                if (map != null) _classConstraints[cls.Name] = map;
            }
            foreach (var m in cls.Methods)
            {
                if (m.TypeParams == null) continue;
                var map = ResolveWhereConstraints(m.Where, m.TypeParams, m.Span);
                if (map != null) _funcConstraints[$"{cls.Name}.{m.Name}"] = map;
            }
        }
    }

    /// Resolve a `where T: BaseClass + I + J, K: I2` clause into a map
    /// `TypeParam → GenericConstraintBundle`. Type expressions in constraints see T as a
    /// generic param via a transient type-param scope (so `where T: IComparable<T>` works
    /// self-referentially).
    /// Reports diagnostics for: unknown type params, invalid constraints (not class/interface),
    /// multiple base classes, or a base class appearing after an interface in the `+` list.
    private IReadOnlyDictionary<string, GenericConstraintBundle>? ResolveWhereConstraints(
        WhereClause? where, IReadOnlyList<string> declaredTypeParams, Span declSpan)
    {
        if (where == null || where.Constraints.Count == 0) return null;

        _symbols.PushTypeParams(declaredTypeParams); // transient, no constraints yet
        try
        {
            var result = new Dictionary<string, GenericConstraintBundle>();
            foreach (var entry in where.Constraints)
            {
                if (!declaredTypeParams.Contains(entry.TypeParam))
                {
                    _diags.Error(DiagnosticCodes.UndefinedSymbol,
                        $"`where` refers to unknown type parameter `{entry.TypeParam}`", entry.Span);
                    continue;
                }
                Z42ClassType? baseClass = null;
                var ifaces = new List<Z42InterfaceType>();
                string? typeParamConstraint = null;
                foreach (var tx in entry.Constraints)
                {
                    // L3-G2.5 bare-typeparam: NamedType matching another active type param
                    // is recorded as a subtype constraint (resolved before class/interface fallback).
                    if (tx is NamedType nt
                        && declaredTypeParams.Contains(nt.Name)
                        && nt.Name != entry.TypeParam) // self-reference handled elsewhere (L3-G2 IComparable<T>)
                    {
                        if (typeParamConstraint != null)
                            _diags.Error(DiagnosticCodes.TypeMismatch,
                                $"generic parameter `{entry.TypeParam}` cannot have multiple type-param constraints",
                                tx.Span);
                        else
                            typeParamConstraint = nt.Name;
                        continue;
                    }
                    var resolved = _symbols.ResolveType(tx);
                    switch (resolved)
                    {
                        case Z42ClassType cc when baseClass != null:
                            _diags.Error(DiagnosticCodes.TypeMismatch,
                                $"generic parameter `{entry.TypeParam}` cannot have multiple class constraints",
                                tx.Span);
                            break;
                        case Z42ClassType cc when ifaces.Count > 0:
                            _diags.Error(DiagnosticCodes.TypeMismatch,
                                $"class constraint `{cc.Name}` must appear first in constraint list for `{entry.TypeParam}`",
                                tx.Span);
                            baseClass = cc; // still record to avoid cascading errors
                            break;
                        case Z42ClassType cc:
                            baseClass = cc;
                            break;
                        case Z42InterfaceType iface:
                            ifaces.Add(iface);
                            break;
                        default:
                            _diags.Error(DiagnosticCodes.TypeMismatch,
                                $"constraint on `{entry.TypeParam}` must be a class or interface, got `{resolved}`",
                                tx.Span);
                            break;
                    }
                }
                // L3-G2.5 refvalue: translate class/struct flags and enforce mutual exclusion.
                bool reqClass  = entry.Kinds.HasFlag(GenericConstraintKind.Class);
                bool reqStruct = entry.Kinds.HasFlag(GenericConstraintKind.Struct);
                bool reqCtor   = entry.Kinds.HasFlag(GenericConstraintKind.Constructor);
                bool reqEnum   = entry.Kinds.HasFlag(GenericConstraintKind.Enum);
                if (reqClass && reqStruct)
                {
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"generic parameter `{entry.TypeParam}` cannot be both `class` and `struct`",
                        entry.Span);
                    reqClass = reqStruct = false; // don't cascade
                }
                // L3-G2.5 enum: mutually exclusive with `class` (enum is value type).
                // `enum` + `struct` is allowed but redundant (enum already implies value type).
                if (reqEnum && reqClass)
                {
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"generic parameter `{entry.TypeParam}` cannot be both `enum` and `class`",
                        entry.Span);
                    reqEnum = reqClass = false;
                }
                if (baseClass != null || ifaces.Count > 0 || reqClass || reqStruct
                    || typeParamConstraint != null || reqCtor || reqEnum)
                    result[entry.TypeParam] = new GenericConstraintBundle(
                        baseClass, ifaces, reqClass, reqStruct, typeParamConstraint, reqCtor, reqEnum);
            }
            return result.Count > 0 ? result : null;
        }
        finally { _symbols.PopTypeParams(); }
    }

    // ── Default parameter binding (Pass 1, not during collection) ───────────

    /// Bind default value expressions and check their types against parameter types.
    private void BindParamDefaults(IReadOnlyList<Param> parms, TypeEnv env)
    {
        for (int i = 0; i < parms.Count; i++)
        {
            var p = parms[i];
            if (p.Default == null) continue;
            var paramType    = _symbols.ResolveType(p.Type);
            var boundDefault = BindExpr(p.Default, env);
            _boundDefaults[p] = boundDefault;
            RequireAssignable(paramType, boundDefault.Type, p.Default.Span,
                $"default value for `{p.Name}`: cannot assign `{boundDefault.Type}` to `{paramType}`");
        }
    }

    private bool ValidateNativeMethod(FunctionDecl fn, bool isInstance = false)
    {
        bool hasNative = fn.NativeIntrinsic != null;
        bool isExtern  = fn.IsExtern;
        if (isExtern && !hasNative)
        {
            _diags.Error(DiagnosticCodes.ExternRequiresNative,
                $"extern method '{fn.Name}' requires a [Native(\"...\")]  attribute", fn.Span);
            return true;
        }
        if (hasNative && !isExtern)
        {
            _diags.Error(DiagnosticCodes.NativeRequiresExtern,
                $"[Native] attribute on '{fn.Name}' requires the extern modifier", fn.Span);
            return false;
        }
        return isExtern;
    }

    private void CheckParamNames(IEnumerable<Param> parms)
    {
        var seen = new HashSet<string>();
        foreach (var p in parms)
            if (!seen.Add(p.Name))
                _diags.Error(DiagnosticCodes.DuplicateDeclaration,
                    $"duplicate parameter name `{p.Name}`", p.Span);
    }

    // ── Static field initializer binding ─────────────────────────────────────

    private void BindStaticFieldInits(CompilationUnit cu)
    {
        foreach (var cls in cu.Classes)
        {
            var statics = cls.Fields.Where(f => f.IsStatic && f.Initializer != null).ToList();
            if (statics.Count == 0) continue;
            var env   = new TypeEnv(_symbols.Functions, _symbols.Classes, _symbols.ImportedClassNames);
            var scope = env.WithClass(cls.Name);
            foreach (var field in statics)
                _boundStaticInits[field] = BindExpr(field.Initializer!, scope);
        }
    }

    // ── Type resolution (delegates to SymbolTable) ───────────────────────────

    private Z42Type ResolveType(TypeExpr typeExpr) => _symbols.ResolveType(typeExpr);

    // ── L3-G4a: type parameter substitution helpers ─────────────────────────

    /// Build substitution map from an instantiated generic type: { TypeParam[i] → TypeArgs[i] }.
    internal static IReadOnlyDictionary<string, Z42Type>? BuildSubstitutionMap(Z42InstantiatedType inst)
    {
        var tps = inst.Definition.TypeParams;
        if (tps is null || tps.Count == 0 || tps.Count != inst.TypeArgs.Count) return null;
        var map = new Dictionary<string, Z42Type>(tps.Count, StringComparer.Ordinal);
        for (int i = 0; i < tps.Count; i++)
            map[tps[i]] = inst.TypeArgs[i];
        return map;
    }

    /// Recursively substitute Z42GenericParamType references in `t` with their concrete
    /// types from `map`. Handles arrays, options, function types, and nested instantiated
    /// types. Returns the input unchanged when no substitution applies.
    internal static Z42Type SubstituteTypeParams(Z42Type t, IReadOnlyDictionary<string, Z42Type>? map)
    {
        if (map is null || map.Count == 0) return t;
        return t switch
        {
            Z42GenericParamType gp   => map.TryGetValue(gp.Name, out var concrete) ? concrete : gp,
            Z42ArrayType arr         => new Z42ArrayType(SubstituteTypeParams(arr.Element, map)),
            Z42OptionType opt        => new Z42OptionType(SubstituteTypeParams(opt.Inner, map)),
            Z42FuncType fn           => new Z42FuncType(
                                             fn.Params.Select(p => SubstituteTypeParams(p, map)).ToList(),
                                             SubstituteTypeParams(fn.Ret, map),
                                             fn.RequiredCount),
            Z42InstantiatedType inst => new Z42InstantiatedType(
                                             inst.Definition,
                                             inst.TypeArgs.Select(a => SubstituteTypeParams(a, map)).ToList()),
            _ => t,
        };
    }

    /// Convert IrType string (from DependencyIndex) to Z42Type.
    /// Maps IR type names like "Str", "I32", "Void" to semantic types.
    private static Z42Type IrTypeToZ42Type(string irType) => irType switch
    {
        "Str"     => Z42Type.String,
        "Bool"    => Z42Type.Bool,
        "Char"    => Z42Type.Char,
        "I8"      => Z42Type.I8,
        "I16"     => Z42Type.I16,
        "I32"     => Z42Type.Int,
        "I64"     => Z42Type.Long,
        "U8"      => Z42Type.U8,
        "U16"     => Z42Type.U16,
        "U32"     => Z42Type.U32,
        "U64"     => Z42Type.U64,
        "F32"     => Z42Type.Float,
        "F64"     => Z42Type.Double,
        "Void"    => Z42Type.Void,
        _         => Z42Type.Unknown  // Unrecognized type defaults to Unknown
    };

    private static Z42Type ElemTypeOf(Z42Type t) => t switch
    {
        Z42ArrayType  at => at.Element,
        Z42OptionType ot => ot.Inner,
        // L3-G4h step2: duck-typed `foreach` — class with `get_Item(int)` indexer
        // yields elements of the indexer's return type (with type-param substitution
        // for instantiated generics).
        Z42InstantiatedType inst when FindIndexerRet(inst.Definition, BuildSubMap(inst)) is { } rt => rt,
        Z42ClassType ct when FindIndexerRet(ct, null) is { } rt => rt,
        _                => Z42Type.Unknown
    };

    private static Z42Type? FindIndexerRet(Z42ClassType ct,
                                           IReadOnlyDictionary<string, Z42Type>? sub)
    {
        if (!ct.Methods.TryGetValue("get_Item", out var mt)) return null;
        return SubstituteTypeParams(mt.Ret, sub);
    }

    private static IReadOnlyDictionary<string, Z42Type> BuildSubMap(Z42InstantiatedType inst)
    {
        var map = new Dictionary<string, Z42Type>();
        var tps = inst.Definition.TypeParams;
        if (tps is not null)
            for (int i = 0; i < tps.Count && i < inst.TypeArgs.Count; i++)
                map[tps[i]] = inst.TypeArgs[i];
        return map;
    }

    // ── Diagnostic helpers ────────────────────────────────────────────────────

    private void RequireBool(Z42Type actual, Span span, string context)
    {
        if (actual is Z42ErrorType or Z42UnknownType) return;
        if (!Z42Type.IsBool(actual))
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"`{context}` condition must be `bool`, got `{actual}`", span);
    }

    private static long? ExtractIntLiteralValue(Expr expr) => expr switch
    {
        LitIntExpr lit                                     => lit.Value,
        UnaryExpr { Op: "-", Operand: LitIntExpr negLit } => -negLit.Value,
        _ => null
    };

    private bool? TryCheckIntLiteralRange(Z42Type target, long value, Span span)
    {
        var range = Z42Type.IntLiteralRange(target);
        if (range == null) return null;
        if (value >= range.Value.Min && value <= range.Value.Max) return true;
        _diags.Error(DiagnosticCodes.IntLiteralOutOfRange,
            $"integer literal `{value}` overflows `{target}` (valid range: {range.Value.Min} to {range.Value.Max})", span);
        return false;
    }

    private void RequireAssignable(Z42Type target, Z42Type source, Span span, string? msg = null)
    {
        if (Z42Type.IsAssignableTo(target, source)) return;
        if (target is Z42ClassType tc1 && source is Z42ClassType tc2 && tc1.Name == tc2.Name) return;
        if (target is Z42ClassType targetCt && source is Z42ClassType sourceCt
            && _symbols.IsSubclassOf(sourceCt.Name, targetCt.Name)) return;
        if (target is Z42InterfaceType targetIface && source is Z42ClassType sourceImplCt
            && _symbols.ImplementsInterface(sourceImplCt.Name, targetIface.Name)) return;
        _diags.Error(DiagnosticCodes.TypeMismatch,
            msg ?? $"cannot assign `{source}` to `{target}`", span);
    }

    private bool IsSubclassOf(string derived, string baseClass) =>
        _symbols.IsSubclassOf(derived, baseClass);

    private bool ImplementsInterface(string className, string interfaceName) =>
        _symbols.ImplementsInterface(className, interfaceName);
}
