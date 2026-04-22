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
        return Infer(cu, symbols);
    }

    /// ITypeInferrer: Pass 1 only — bind bodies using an already-collected SymbolTable.
    public SemanticModel Infer(CompilationUnit cu, SymbolTable symbols)
    {
        _symbols = symbols;

        // ── Pass 0.5: resolve generic constraints from `where` clauses (L3-G2) ──
        ResolveAllWhereConstraints(cu);

        // ── Pass 1: bind bodies (function-level error isolation) ────────────
        BindStaticFieldInits(cu);
        foreach (var cls in cu.Classes)   TryBindClassMethods(cls);
        foreach (var fn  in cu.Functions) TryBindFunction(fn);

        // ── Assemble SemanticModel from frozen symbols + bound results ──────
        return new SemanticModel(
            _symbols.Classes, _symbols.Functions, _symbols.Interfaces,
            _symbols.EnumConstants, _symbols.EnumTypes,
            _boundBodies, _boundDefaults, _boundStaticInits, _boundBaseCtorArgs,
            _symbols.ImportedClassNamespaces as Dictionary<string, string>
                ?? new Dictionary<string, string>(_symbols.ImportedClassNamespaces),
            funcConstraints:  _funcConstraints,
            classConstraints: _classConstraints);
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
                if (!TypeSatisfiesInterface(typeArg, iface))
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `{iface.Name}` on `{declName}`",
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

    /// L3-G2.5 refvalue: is `typeArg` a reference type (for `where T: class`)?
    private static bool IsClassArg(Z42Type t) => t switch
    {
        Z42ClassType ct               => !ct.IsStruct,
        Z42ErrorType or Z42UnknownType => true, // don't cascade
        _                             => Z42Type.IsReferenceType(t),
    };

    /// L3-G2.5 refvalue: is `typeArg` a value type (for `where T: struct`)?
    private static bool IsStructArg(Z42Type t) => t switch
    {
        Z42ClassType ct               => ct.IsStruct,
        Z42PrimType                   => !Z42Type.IsReferenceType(t), // int/bool/float/...
        Z42ErrorType or Z42UnknownType => true,
        _                             => false,
    };

    /// Does `typeArg` satisfy the interface constraint `iface`?
    /// - Class type: via SymbolTable.ImplementsInterface (walks hierarchy).
    /// - Interface type: same-name match (interface extending not tracked yet — L3-G3).
    /// - Generic param: accept if one of its own constraints is this interface (propagate).
    /// - Everything else (primitive, array, etc.): false for now (primitives handled in L3-G4).
    private bool TypeSatisfiesInterface(Z42Type typeArg, Z42InterfaceType iface) => typeArg switch
    {
        Z42ClassType ct       => _symbols.ImplementsInterface(ct.Name, iface.Name),
        Z42InterfaceType it   => it.Name == iface.Name,
        Z42GenericParamType g => g.InterfaceConstraints?.Any(c => c.Name == iface.Name) == true,
        Z42ErrorType          => true, // don't cascade errors
        Z42UnknownType        => true,
        _                     => false,
    };

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
                if (reqClass && reqStruct)
                {
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"generic parameter `{entry.TypeParam}` cannot be both `class` and `struct`",
                        entry.Span);
                    reqClass = reqStruct = false; // don't cascade
                }
                if (baseClass != null || ifaces.Count > 0 || reqClass || reqStruct
                    || typeParamConstraint != null)
                    result[entry.TypeParam] = new GenericConstraintBundle(
                        baseClass, ifaces, reqClass, reqStruct, typeParamConstraint);
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
        _                => Z42Type.Unknown
    };

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
