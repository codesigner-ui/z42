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
                ?? new Dictionary<string, string>(_symbols.ImportedClassNamespaces));
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
        if (cls.TypeParams != null) _symbols.PushTypeParams(cls.TypeParams);
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
        if (fn.TypeParams != null) _symbols.PushTypeParams(fn.TypeParams);
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
