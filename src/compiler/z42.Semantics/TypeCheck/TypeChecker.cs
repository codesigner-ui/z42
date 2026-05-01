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
/// • TypeChecker.cs              — orchestration, body entry points, helpers
/// • TypeChecker.Stmts.cs        — statement binding
/// • TypeChecker.Exprs.cs        — expression type inference
/// • TypeChecker.Calls.cs        — call & argument binding
/// • TypeChecker.Generics.cs     — generic constraint validation + type-param substitution
/// • TypeChecker.GenericResolve.cs — `where`-clause resolution + imported constraint rehydration
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

    /// Stack of `outer-env at lambda boundary` for L2 no-capture detection.
    /// Pushed when entering a lambda body; popped when leaving. Empty means we're
    /// not inside a lambda. See docs/design/closure.md §10 + design.md Decision 6.
    private readonly Stack<TypeEnv> _lambdaOuterStack = new();

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
        // strict-using-resolution (2026-04-28): emit E0601 for cross-package
        // (namespace, class-name) collisions; E0602 for `using <ns>;` that no
        // loaded package provides.
        if (imported is not null)
        {
            EmitImportDiagnostics(cu, imported);
        }
        var binder  = new SymbolCollector(_diags);
        var symbols = binder.Collect(cu, imported);
        // L3-G3d: rehydrate imported `where` constraints so ValidateGenericConstraints
        // fires for `new ImportedGeneric<T>()` / imported generic func calls.
        _imported = imported;
        return Infer(cu, symbols);
    }

    /// strict-using-resolution: emit E0601 (collisions) + E0602 (unresolved using).
    private void EmitImportDiagnostics(CompilationUnit cu, ImportedSymbols imported)
    {
        // E0601 — every collision becomes an error, attached to the file's first
        // line span (no per-using span yet; future: thread span through ImportedSymbols).
        if (imported.Collisions is { Count: > 0 } cols)
        {
            var span = new Z42.Core.Text.Span(0, 0, 0, 0, "");
            foreach (var c in cols)
            {
                _diags.Error(DiagnosticCodes.NamespaceCollision,
                    $"class `{c.Namespace}.{c.ClassName}` is provided by multiple packages: " +
                    $"[{string.Join(", ", c.Packages)}]; rename or restrict using",
                    span);
            }
        }

        // E0602 — using points to no loaded namespace.
        // Resolved namespaces = ImportedSymbols.ClassNamespaces values + intra-package own namespace.
        if (cu.Usings.Count == 0) return;
        var resolvedNs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in imported.ClassNamespaces.Values) resolvedNs.Add(v);
        // 同包文件相互 using 也应该被认为已解析（intraSymbols 通过 ClassNamespaces 注入）
        if (cu.Namespace is { } ownNs) resolvedNs.Add(ownNs);
        var unresolvedSpan = new Z42.Core.Text.Span(0, 0, 0, 0, "");
        foreach (var u in cu.Usings)
        {
            if (!resolvedNs.Contains(u))
                _diags.Error(DiagnosticCodes.UnresolvedUsing,
                    $"using `{u}`: no loaded package provides this namespace",
                    unresolvedSpan);
        }
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
                    // L3 primitive-as-struct: `this` uses primitive type for stdlib's
                    // `impl Trait for int { ... }` so in-body arithmetic type-checks.
                    // PascalCase class names (String / Char etc.) also map to primitives.
                    var thisType = TypeRegistry.GetZ42Type(targetNt.Name)
                                ?? TypeRegistry.GetZ42Type(targetNt.Name.ToLowerInvariant())
                                ?? (Z42Type)targetClass;
                    scope.Define("this", thisType);
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
            // Spec C9 — pass class-level [Native(lib=, type=)] defaults so
            // the method's possibly-partial Tier1 binding can be validated
            // against the stitched whole.
            if (ValidateNativeMethod(method, isInstance: !method.IsStatic,
                classNativeDefaults: cls.ClassNativeDefaults)) continue;
            var env   = new TypeEnv(_symbols.Functions, _symbols.Classes, _symbols.ImportedClassNames);
            var scope = env.WithClass(cls.Name);
            if (!method.IsStatic)
            {
                // L3 primitive-as-struct: for stdlib `struct int { ... }` etc., `this`
                // inside method bodies should be the primitive type (Z42PrimType), not
                // the class type — so `this + other` and similar arithmetic type-check.
                // Also accept PascalCase class names (e.g. `class String` → primitive
                // `string`) so script-side instance methods can use `this.Length` etc.
                var thisType = TypeRegistry.GetZ42Type(cls.Name)
                            ?? TypeRegistry.GetZ42Type(cls.Name.ToLowerInvariant())
                            ?? (Z42Type)classType;
                scope.Define("this", thisType);
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

    private bool ValidateNativeMethod(FunctionDecl fn, bool isInstance = false,
        Tier1NativeBinding? classNativeDefaults = null)
    {
        // Spec C6 — `[Native]` accepts two mutually-exclusive forms:
        //   • legacy: `[Native("__name")]`         → fn.NativeIntrinsic
        //   • Tier 1: `[Native(lib=, type=, entry=)]` → fn.Tier1Binding
        // Spec C9 — Tier 1 binding may be partial; class-level
        // `[Native(lib=, type=)]` provides defaults for missing fields.
        var stitched = StitchTier1(fn.Tier1Binding, classNativeDefaults);
        bool hasNative = fn.NativeIntrinsic != null || stitched != null;
        bool isExtern  = fn.IsExtern;
        if (isExtern && !hasNative)
        {
            _diags.Error(DiagnosticCodes.ExternRequiresNative,
                $"extern method '{fn.Name}' requires a [Native(\"...\")] or [Native(lib=, type=, entry=)] attribute",
                fn.Span);
            return true;
        }
        if (hasNative && !isExtern)
        {
            _diags.Error(DiagnosticCodes.NativeRequiresExtern,
                $"[Native] attribute on '{fn.Name}' requires the extern modifier", fn.Span);
            return false;
        }
        // Tier 1 stitched binding must be complete.
        if (stitched is not null
            && (stitched.Lib is null || stitched.TypeName is null || stitched.Entry is null))
        {
            var missing = new List<string>();
            if (stitched.Lib is null)      missing.Add("lib");
            if (stitched.TypeName is null) missing.Add("type");
            if (stitched.Entry is null)    missing.Add("entry");
            _diags.Error(DiagnosticCodes.NativeAttributeMalformed,
                $"`[Native]` on extern method `{fn.Name}` is incomplete after stitching with class defaults; missing: {string.Join(", ", missing)}",
                fn.Span);
        }
        return isExtern;
    }

    /// Same logic as `IrGen.StitchTier1` — duplicated to avoid a TypeCheck →
    /// Codegen dependency. Method fields override class defaults.
    private static Tier1NativeBinding? StitchTier1(
        Tier1NativeBinding? methodBinding,
        Tier1NativeBinding? classDefaults)
    {
        if (methodBinding is null && classDefaults is null) return null;
        return new Tier1NativeBinding(
            Lib:      methodBinding?.Lib      ?? classDefaults?.Lib,
            TypeName: methodBinding?.TypeName ?? classDefaults?.TypeName,
            Entry:    methodBinding?.Entry    ?? classDefaults?.Entry);
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
        // ClassType → InterfaceType: TypeArgs-aware match via declared interfaces.
        if (target is Z42InterfaceType tIface && source is Z42ClassType srcCt
            && ClassImplementsInterfaceWithArgs(srcCt.Name, null, tIface)) return;
        // InstantiatedType → InterfaceType: substitute class TypeArgs into declared
        // interface TypeArgs, then compare. (`MyList<int>` → `IEnumerable<int>`.)
        if (target is Z42InterfaceType tIface2 && source is Z42InstantiatedType srcInst
            && ClassImplementsInterfaceWithArgs(
                srcInst.Definition.Name, BuildSubstitutionMap(srcInst), tIface2)) return;
        _diags.Error(DiagnosticCodes.TypeMismatch,
            msg ?? $"cannot assign `{source}` to `{target}`", span);
    }

    /// 检查 `className`（含可选实例化 substitution）是否声明实现了
    /// `target` 接口（含 TypeArgs 比较）。复用现有
    /// `ImplementedInterfacesByName` + `InterfacesEqual`。
    /// 当 target.TypeArgs 为 null 时退化为 name-only 兼容（向后兼容非泛型接口）。
    private bool ClassImplementsInterfaceWithArgs(
        string className,
        IReadOnlyDictionary<string, Z42Type>? classSub,
        Z42InterfaceType target)
    {
        bool targetHasArgs = target.TypeArgs is { Count: > 0 };
        foreach (var declared in _symbols.ImplementedInterfacesByName(className, target.Name))
        {
            if (!targetHasArgs) return true;  // non-generic interface: name-only
            // 把 declared 的 TypeArgs 按 class subMap substitute，再与 target 比较
            var declaredEffective = classSub is null
                ? declared
                : SubstituteInterfaceTypeArgs(declared, classSub);
            if (InterfacesEqual(declaredEffective, target)) return true;
        }
        return false;
    }

    private bool IsSubclassOf(string derived, string baseClass) =>
        _symbols.IsSubclassOf(derived, baseClass);

    private bool ImplementsInterface(string className, string interfaceName) =>
        _symbols.ImplementsInterface(className, interfaceName);
}
