using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Syntax.Lexer;
using Z42.Core.Diagnostics;
using Z42.Syntax.Parser;

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
public sealed partial class TypeChecker
{
    private readonly DiagnosticBag   _diags;
    private readonly LanguageFeatures _features;

    // ── Readonly symbol table (populated by SymbolCollector, consumed here) ───
    private SymbolTable _symbols = null!;

    // ── Binding outputs ──────────────────────────────────────────────────────
    private readonly Dictionary<FunctionDecl, BoundBlock> _boundBodies = new();
    private readonly Dictionary<Param, BoundExpr> _boundDefaults =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FieldDecl, BoundExpr> _boundStaticInits =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionDecl, IReadOnlyList<BoundExpr>> _boundBaseCtorArgs = new();

    // ── Binding state ────────────────────────────────────────────────────────
    private string? _currentClass;

    public TypeChecker(DiagnosticBag diags, LanguageFeatures? features = null)
    {
        _diags    = diags;
        _features = features ?? LanguageFeatures.Phase1;
    }

    /// Sets <see cref="_currentClass"/> and returns a scope that clears it on disposal.
    private IDisposable EnterClass(string name)
    {
        _currentClass = name;
        return new ClassScope(this);
    }

    private sealed class ClassScope(TypeChecker tc) : IDisposable
    {
        public void Dispose() => tc._currentClass = null;
    }

    /// The frozen symbol table, available after Check() begins Pass 1.
    internal SymbolTable Symbols => _symbols;

    // ── Public entry point ────────────────────────────────────────────────────

    public SemanticModel Check(CompilationUnit cu)
    {
        // ── Pass 0: collect type shapes (SymbolCollector) ───────────────────
        var collector = new SymbolCollector(_diags);
        _symbols = collector.Collect(cu);

        // ── Pass 1: bind bodies (function-level error isolation) ────────────
        BindStaticFieldInits(cu);
        foreach (var cls in cu.Classes)   TryBindClassMethods(cls);
        foreach (var fn  in cu.Functions) TryBindFunction(fn);

        // ── Assemble SemanticModel from frozen symbols + bound results ──────
        return new SemanticModel(
            _symbols.Classes, _symbols.Functions, _symbols.Interfaces,
            _symbols.EnumConstants, _symbols.EnumTypes,
            _boundBodies, _boundDefaults, _boundStaticInits, _boundBaseCtorArgs);
    }

    // ── Body binding entry points (error-isolated) ──────────────────────────

    private void TryBindClassMethods(ClassDecl cls)
    {
        try { BindClassMethods(cls); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diags.Error(DiagnosticCodes.UnsupportedSyntax,
                $"internal error checking class `{cls.Name}`: {ex.Message}", cls.Span);
        }
    }

    private void TryBindFunction(FunctionDecl fn)
    {
        try { BindFunction(fn); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diags.Error(DiagnosticCodes.UnsupportedSyntax,
                $"internal error checking function `{fn.Name}`: {ex.Message}", fn.Span);
        }
    }

    private void BindClassMethods(ClassDecl cls)
    {
        if (!_symbols.Classes.TryGetValue(cls.Name, out var classType)) return;
        using var _ = EnterClass(cls.Name);
        foreach (var method in cls.Methods)
        {
            if (ValidateNativeMethod(method, isInstance: !method.IsStatic)) continue;
            var env   = new TypeEnv(_symbols.Functions, _symbols.Classes);
            var scope = env.PushScope();
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

    private void BindFunction(FunctionDecl fn)
    {
        if (ValidateNativeMethod(fn, isInstance: false)) return;
        var env   = new TypeEnv(_symbols.Functions, _symbols.Classes);
        var scope = env.PushScope();
        CheckParamNames(fn.Params);
        foreach (var p in fn.Params)
            scope.Define(p.Name, _symbols.ResolveType(p.Type));
        BindParamDefaults(fn.Params, scope);
        _boundBodies[fn] = BindBlock(fn.Body, scope, _symbols.ResolveType(fn.ReturnType));
        var fnRetType = _symbols.ResolveType(fn.ReturnType);
        if (fnRetType is not Z42VoidType and not Z42UnknownType and not Z42ErrorType
            && !FlowAnalyzer.AlwaysReturns(_boundBodies[fn]))
            _diags.Error(DiagnosticCodes.MissingReturn,
                $"not all code paths return a value in `{fn.Name}`", fn.Span);
        FlowAnalyzer.CheckDefiniteAssignment(_boundBodies[fn], _diags);
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
            using var _ = EnterClass(cls.Name);
            var env   = new TypeEnv(_symbols.Functions, _symbols.Classes);
            var scope = env.PushScope();
            foreach (var field in statics)
                _boundStaticInits[field] = BindExpr(field.Initializer!, scope);
        }
    }

    // ── Type resolution (delegates to SymbolTable) ───────────────────────────

    private Z42Type ResolveType(TypeExpr typeExpr) => _symbols.ResolveType(typeExpr);

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
