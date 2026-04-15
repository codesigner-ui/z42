using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Core.Diagnostics;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Phase 1 type checker.
///
/// Two-pass design:
///   Pass 0  — collect enum constants, class shapes, function signatures.
///   Pass 1  — walk each function body, infer expression types, check compatibility.
///
/// Errors are reported to the DiagnosticBag; the checker continues after errors
/// using Z42Type.Error as a sentinel to suppress cascading diagnostics.
///
/// Implementation is split across partial class files:
/// • TypeChecker.cs       — entry point, collection passes, type resolution, helpers
/// • TypeChecker.Stmts.cs — statement checking
/// • TypeChecker.Exprs.cs — expression type inference
/// </summary>
public sealed partial class TypeChecker
{
    private readonly DiagnosticBag             _diags;
    private readonly LanguageFeatures          _features;
    private          Dictionary<string, Z42FuncType>      _funcs      = new();
    private          Dictionary<string, Z42ClassType>     _classes    = new();
    private          Dictionary<string, Z42InterfaceType> _interfaces = new();
    /// Expression → inferred type, keyed by object identity (not structural equality).
    private readonly Dictionary<Expr, Z42Type> _exprTypes =
        new(ReferenceEqualityComparer.Instance);
    /// class name → set of interface names the class declares it implements
    private          Dictionary<string, HashSet<string>>  _classInterfaces = new();
    /// class name → set of abstract method names (inherited + own)
    private          Dictionary<string, HashSet<string>>  _abstractMethods = new();
    /// set of abstract class names
    private          HashSet<string>                      _abstractClasses = new();
    /// set of sealed class names (cannot be subclassed)
    private          HashSet<string>                      _sealedClasses   = new();
    /// class name → set of virtual/abstract method names (own only)
    private          Dictionary<string, HashSet<string>>  _virtualMethods  = new();
    private readonly Dictionary<string, long>             _globalEnumConstants = new();
    private readonly HashSet<string>                      _enumTypes           = new();
    /// The class currently being type-checked (null when checking top-level functions).
    private          string?                              _currentClass        = null;

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

    // ── Public entry point ────────────────────────────────────────────────────

    public SemanticModel Check(CompilationUnit cu)
    {
        CollectEnums(cu);
        CollectInterfaces(cu);
        CollectClasses(cu);
        CollectFunctions(cu);
        foreach (var cls in cu.Classes)   CheckClassMethods(cls);
        foreach (var fn  in cu.Functions) CheckFunction(fn);
        return new SemanticModel(_classes, _funcs, _interfaces,
            _globalEnumConstants, _enumTypes, _exprTypes);
    }

    // ── Pass 0a: enum constants ───────────────────────────────────────────────

    private void CollectEnums(CompilationUnit cu)
    {
        foreach (var en in cu.Enums)
        {
            foreach (var m in en.Members)
                _globalEnumConstants[$"{en.Name}.{m.Name}"] = m.Value ?? 0;
            _enumTypes.Add(en.Name);
        }
    }

    // ── Pass 0b: interface shapes ─────────────────────────────────────────────

    private void CollectInterfaces(CompilationUnit cu)
    {
        foreach (var iface in cu.Interfaces)
        {
            var methods = new Dictionary<string, Z42FuncType>();
            foreach (var m in iface.Methods)
            {
                methods[m.Name] = BuildFuncType(m.Params, ResolveType(m.ReturnType));
            }
            _interfaces[iface.Name] = new Z42InterfaceType(iface.Name, methods);
        }
    }

    // ── Pass 0c: class shapes — see TypeChecker.Classes.cs ──────────────────

    // ── Pass 1: function signatures ───────────────────────────────────────────

    private void CollectFunctions(CompilationUnit cu)
    {
        foreach (var fn in cu.Functions)
        {
            if (_funcs.ContainsKey(fn.Name))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"duplicate function declaration `{fn.Name}`", fn.Span);
            _funcs[fn.Name] = BuildFuncType(fn.Params, ResolveType(fn.ReturnType));
        }
    }

    // ── Body checking entry points ────────────────────────────────────────────

    private void CheckClassMethods(ClassDecl cls)
    {
        if (!_classes.TryGetValue(cls.Name, out var classType)) return;
        using var _ = EnterClass(cls.Name);
        foreach (var method in cls.Methods)
        {
            if (ValidateNativeMethod(method, isInstance: !method.IsStatic)) continue; // extern: skip body check
            var env   = new TypeEnv(_funcs, _classes);
            var scope = env.PushScope();
            if (!method.IsStatic)
            {
                // Instance method: `this` is in scope, as are instance fields
                scope.Define("this", classType);
                foreach (var (fname, ftype) in classType.Fields)
                    scope.Define(fname, ftype);
            }
            CheckParamNames(method.Params);
            foreach (var p in method.Params)
                scope.Define(p.Name, ResolveType(p.Type));
            bool isCtor = method.Name == cls.Name;
            CheckBlock(method.Body, scope, isCtor ? Z42Type.Void : ResolveType(method.ReturnType));
        }
    }

    private void CheckFunction(FunctionDecl fn)
    {
        if (ValidateNativeMethod(fn, isInstance: false)) return; // extern: skip body check
        var env   = new TypeEnv(_funcs, _classes);
        var scope = env.PushScope();
        CheckParamNames(fn.Params);
        foreach (var p in fn.Params)
            scope.Define(p.Name, ResolveType(p.Type));
        CheckBlock(fn.Body, scope, ResolveType(fn.ReturnType));
    }

    /// Validates [Native] / extern consistency.
    /// Returns true if the method is a valid extern (body check should be skipped).
    /// Returns false if it is a regular method (no extern/Native issues found, continue normal check).
    /// <param name="isInstance">True for instance methods (adds 1 for implicit `this` when checking param count).</param>
    private bool ValidateNativeMethod(FunctionDecl fn, bool isInstance = false)
    {
        bool hasNative = fn.NativeIntrinsic != null;
        bool isExtern  = fn.IsExtern;

        if (isExtern && !hasNative)
        {
            _diags.Error(DiagnosticCodes.ExternRequiresNative,
                $"extern method '{fn.Name}' requires a [Native(\"...\")]  attribute", fn.Span);
            return true; // skip body check regardless
        }
        if (hasNative && !isExtern)
        {
            _diags.Error(DiagnosticCodes.NativeRequiresExtern,
                $"[Native] attribute on '{fn.Name}' requires the extern modifier", fn.Span);
            return false;
        }
        if (!isExtern) return false; // plain method, no native concerns

        // Both isExtern and hasNative — valid extern, skip body check.
        // Intrinsic name validation is deferred to the VM (no NativeTable lookup).
        return true;
    }

    /// Reports an error for any duplicate parameter name in a function / method.
    private void CheckParamNames(IEnumerable<Param> parms)
    {
        var seen = new HashSet<string>();
        foreach (var p in parms)
            if (!seen.Add(p.Name))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"duplicate parameter name `{p.Name}`", p.Span);
    }

    // ── Type resolution ───────────────────────────────────────────────────────

    private Z42Type ResolveType(TypeExpr typeExpr) => typeExpr switch
    {
        VoidType      => Z42Type.Void,
        OptionType ot => new Z42OptionType(ResolveType(ot.Inner)),
        ArrayType  at => new Z42ArrayType(ResolveType(at.Element)),
        NamedType  nt => nt.Name switch
        {
            "int"    or "i32" => Z42Type.Int,
            "long"   or "i64" => Z42Type.Long,
            "float"  or "f32" => Z42Type.Float,
            "double" or "f64" => Z42Type.Double,
            "bool"            => Z42Type.Bool,
            "string"          => Z42Type.String,
            "char"            => Z42Type.Char,
            "object"          => Z42Type.Object,
            "void"            => Z42Type.Void,
            "var"             => Z42Type.Unknown,
            // IR names
            "i8"              => Z42Type.I8,
            "i16"             => Z42Type.I16,
            "u8"              => Z42Type.U8,
            "u16"             => Z42Type.U16,
            "u32"             => Z42Type.U32,
            "u64"             => Z42Type.U64,
            // C# aliases → IR equivalents
            "sbyte"           => Z42Type.I8,
            "short"           => Z42Type.I16,
            "byte"            => Z42Type.U8,
            "ushort"          => Z42Type.U16,
            "uint"            => Z42Type.U32,
            "ulong"           => Z42Type.U64,
            _                 => _classes.TryGetValue(nt.Name, out var ct)    ? (Z42Type)ct
                               : _interfaces.TryGetValue(nt.Name, out var it) ? it
                               : new Z42PrimType(nt.Name),
        },
        _ => Z42Type.Unknown
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

    /// Extracts the integer value from a literal expression.
    /// Handles plain <c>LitIntExpr</c> and negated <c>UnaryExpr("-", LitIntExpr(x))</c>.
    /// Returns null for any other expression kind.
    private static long? ExtractIntLiteralValue(Expr expr) => expr switch
    {
        LitIntExpr lit                                     => lit.Value,
        UnaryExpr { Op: "-", Operand: LitIntExpr negLit } => -negLit.Value,
        _ => null
    };

    /// Checks whether an integer literal value fits into <paramref name="target"/>'s range.
    /// Returns true  — value fits, caller can skip RequireAssignable.
    /// Returns false — out of range, error already emitted.
    /// Returns null  — target has no integer literal range; caller should use RequireAssignable.
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
        // Same-named class types are identical even if the instances differ
        // (can happen with self-referential fields: `Node next` inside class Node
        //  resolves to a stub instance before the full type is registered).
        if (target is Z42ClassType tc1 && source is Z42ClassType tc2 && tc1.Name == tc2.Name) return;
        // Inheritance: source class is a subtype of target class
        if (target is Z42ClassType targetCt && source is Z42ClassType sourceCt
            && IsSubclassOf(sourceCt.Name, targetCt.Name)) return;
        // Interface: source class implements target interface
        if (target is Z42InterfaceType targetIface && source is Z42ClassType sourceImplCt
            && ImplementsInterface(sourceImplCt.Name, targetIface.Name)) return;
        _diags.Error(DiagnosticCodes.TypeMismatch,
            msg ?? $"cannot assign `{source}` to `{target}`", span);
    }

    /// Returns true if <paramref name="derived"/> is a subclass of <paramref name="baseClass"/>
    /// (walks the inheritance chain).
    private bool IsSubclassOf(string derived, string baseClass)
    {
        var cur = derived;
        while (_classes.TryGetValue(cur, out var ct) && ct.BaseClassName is { } parentName)
        {
            if (parentName == baseClass) return true;
            cur = parentName;
        }
        return false;
    }

    /// Returns true if <paramref name="className"/> (or any ancestor) declares it implements
    /// <paramref name="interfaceName"/>.
    private bool ImplementsInterface(string className, string interfaceName)
    {
        var cur = className;
        while (true)
        {
            if (_classInterfaces.TryGetValue(cur, out var ifaces) && ifaces.Contains(interfaceName))
                return true;
            if (!_classes.TryGetValue(cur, out var ct) || ct.BaseClassName == null) break;
            cur = ct.BaseClassName;
        }
        return false;
    }

    /// Build a Z42FuncType from a parameter list, checking default value types and computing RequiredCount.
    private Z42FuncType BuildFuncType(IReadOnlyList<Param> parms, Z42Type retType, TypeEnv? env = null)
    {
        var paramTypes    = parms.Select(p => ResolveType(p.Type)).ToList();
        int requiredCount = parms.Count;

        for (int i = 0; i < parms.Count; i++)
        {
            var p = parms[i];
            if (p.Default != null)
            {
                if (i < requiredCount) requiredCount = i;
                // Check default value type against parameter type
                var defaultEnv   = env ?? new TypeEnv(_funcs, _classes);
                var defaultType  = CheckExpr(p.Default, defaultEnv);
                RequireAssignable(paramTypes[i], defaultType, p.Default.Span,
                    $"default value for `{p.Name}`: cannot assign `{defaultType}` to `{paramTypes[i]}`");
            }
            else if (i >= requiredCount)
            {
                // Non-default parameter after a defaulted one — error
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"non-default parameter `{p.Name}` follows a default parameter", p.Span);
            }
        }

        return new Z42FuncType(paramTypes, retType, requiredCount == parms.Count ? -1 : requiredCount);
    }
}
