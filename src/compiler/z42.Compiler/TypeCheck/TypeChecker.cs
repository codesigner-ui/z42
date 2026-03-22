using Z42.Compiler.Lexer;
using Z42.Compiler.Diagnostics;
using Z42.Compiler.Parser;

namespace Z42.Compiler.TypeCheck;

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
    private          Dictionary<string, Z42FuncType>  _funcs   = new();
    private          Dictionary<string, Z42ClassType> _classes = new();
    private readonly Dictionary<string, long>         _globalEnumConstants = new();
    private readonly HashSet<string>                  _enumTypes           = new();
    /// The class currently being type-checked (null when checking top-level functions).
    private          string?                          _currentClass        = null;

    public TypeChecker(DiagnosticBag diags) => _diags = diags;

    // ── Public entry point ────────────────────────────────────────────────────

    public void Check(CompilationUnit cu)
    {
        CollectEnums(cu);
        CollectClasses(cu);
        CollectFunctions(cu);
        foreach (var cls in cu.Classes)   CheckClassMethods(cls);
        foreach (var fn  in cu.Functions) CheckFunction(fn);
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

    // ── Pass 0b: class shapes ─────────────────────────────────────────────────

    private void CollectClasses(CompilationUnit cu)
    {
        foreach (var cls in cu.Classes)
        {
            var fields  = cls.Fields.ToDictionary(f => f.Name, f => ResolveType(f.Type));
            var methods = new Dictionary<string, Z42FuncType>();
            foreach (var m in cls.Methods)
            {
                var paramTypes = m.Params.Select(p => ResolveType(p.Type)).ToList();
                var retType    = m.Name == cls.Name ? (Z42Type)Z42Type.Void : ResolveType(m.ReturnType);
                methods[m.Name] = new Z42FuncType(paramTypes, retType);
            }
            var memberVis = new Dictionary<string, Visibility>();
            foreach (var f in cls.Fields)  memberVis[f.Name] = f.Visibility;
            foreach (var m in cls.Methods) memberVis[m.Name] = m.Visibility;
            _classes[cls.Name] = new Z42ClassType(cls.Name, fields, methods, memberVis);
        }
    }

    // ── Pass 1: function signatures ───────────────────────────────────────────

    private void CollectFunctions(CompilationUnit cu)
    {
        foreach (var fn in cu.Functions)
        {
            var paramTypes = fn.Params.Select(p => ResolveType(p.Type)).ToList();
            _funcs[fn.Name] = new Z42FuncType(paramTypes, ResolveType(fn.ReturnType));
        }
    }

    // ── Body checking entry points ────────────────────────────────────────────

    private void CheckClassMethods(ClassDecl cls)
    {
        if (!_classes.TryGetValue(cls.Name, out var classType)) return;
        _currentClass = cls.Name;
        foreach (var method in cls.Methods)
        {
            var env   = new TypeEnv(_funcs, _classes);
            var scope = env.PushScope();
            scope.Define("this", classType);
            foreach (var (fname, ftype) in classType.Fields)
                scope.Define(fname, ftype);
            foreach (var p in method.Params)
                scope.Define(p.Name, ResolveType(p.Type));
            bool isCtor = method.Name == cls.Name;
            CheckBlock(method.Body, scope, isCtor ? Z42Type.Void : ResolveType(method.ReturnType));
        }
        _currentClass = null;
    }

    private void CheckFunction(FunctionDecl fn)
    {
        var env   = new TypeEnv(_funcs, _classes);
        var scope = env.PushScope();
        foreach (var p in fn.Params)
            scope.Define(p.Name, ResolveType(p.Type));
        CheckBlock(fn.Body, scope, ResolveType(fn.ReturnType));
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
            _                 => _classes.TryGetValue(nt.Name, out var ct)
                                  ? ct
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

    private void RequireAssignable(Z42Type target, Z42Type source, Span span, string? msg = null)
    {
        if (!Z42Type.IsAssignableTo(target, source))
            _diags.Error(DiagnosticCodes.TypeMismatch,
                msg ?? $"cannot assign `{source}` to `{target}`", span);
    }
}
