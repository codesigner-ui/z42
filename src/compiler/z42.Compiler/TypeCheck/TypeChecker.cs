using Z42.Compiler.Diagnostics;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;

namespace Z42.Compiler.TypeCheck;

/// <summary>
/// Phase 1 type checker.
///
/// Two-pass design:
///   Pass 1  — collect all function signatures (so functions can be called before declaration).
///   Pass 2  — walk each function body, infer expression types, check compatibility.
///
/// Errors are reported to the supplied <see cref="DiagnosticBag"/>; the checker
/// continues after errors using the <see cref="Z42Type.Error"/> sentinel to suppress
/// cascading diagnostics.
/// </summary>
public sealed class TypeChecker
{
    private readonly DiagnosticBag _diags;
    private Dictionary<string, Z42FuncType>  _funcs   = new();
    private Dictionary<string, Z42ClassType>  _classes = new();

    public TypeChecker(DiagnosticBag diags) => _diags = diags;

    // ── Public entry point ────────────────────────────────────────────────────

    public void Check(CompilationUnit cu)
    {
        CollectClasses(cu);
        CollectFunctions(cu);
        foreach (var cls in cu.Classes)
            CheckClassMethods(cls);
        foreach (var fn in cu.Functions)
            CheckFunction(fn);
    }

    // ── Pass 0: collect class shapes ─────────────────────────────────────────

    private void CollectClasses(CompilationUnit cu)
    {
        foreach (var cls in cu.Classes)
        {
            var fields  = cls.Fields.ToDictionary(f => f.Name, f => ResolveType(f.Type));
            var methods = new Dictionary<string, Z42FuncType>();
            foreach (var m in cls.Methods)
            {
                // Constructor has same name as class; `this` is implicit (not in params list)
                var paramTypes = m.Params.Select(p => ResolveType(p.Type)).ToList();
                var retType    = m.Name == cls.Name ? (Z42Type)Z42Type.Void : ResolveType(m.ReturnType);
                methods[m.Name] = new Z42FuncType(paramTypes, retType);
            }
            _classes[cls.Name] = new Z42ClassType(cls.Name, fields, methods);
        }
    }

    // ── Pass 1: collect signatures ────────────────────────────────────────────

    private void CollectFunctions(CompilationUnit cu)
    {
        foreach (var fn in cu.Functions)
        {
            var paramTypes = fn.Params.Select(p => ResolveType(p.Type)).ToList();
            var retType    = ResolveType(fn.ReturnType);
            _funcs[fn.Name] = new Z42FuncType(paramTypes, retType);
        }
    }

    // ── Class method body checking ────────────────────────────────────────────

    private void CheckClassMethods(ClassDecl cls)
    {
        if (!_classes.TryGetValue(cls.Name, out var classType)) return;
        foreach (var method in cls.Methods)
        {
            var env   = new TypeEnv(_funcs, _classes);
            var scope = env.PushScope();
            // Inject `this` as first implicit parameter
            scope.Define("this", classType);
            // Inject field names into scope (accessible without `this.` prefix in methods)
            foreach (var (fname, ftype) in classType.Fields)
                scope.Define(fname, ftype);
            foreach (var p in method.Params)
                scope.Define(p.Name, ResolveType(p.Type));
            bool isCtor = method.Name == cls.Name;
            var retType = isCtor ? Z42Type.Void : ResolveType(method.ReturnType);
            CheckBlock(method.Body, scope, retType);
        }
    }

    // ── Pass 2: check bodies ──────────────────────────────────────────────────

    private void CheckFunction(FunctionDecl fn)
    {
        var env     = new TypeEnv(_funcs, _classes);
        var scope   = env.PushScope();
        var retType = ResolveType(fn.ReturnType);

        foreach (var p in fn.Params)
            scope.Define(p.Name, ResolveType(p.Type));

        CheckBlock(fn.Body, scope, retType);
    }

    // ── Statements ────────────────────────────────────────────────────────────

    private void CheckBlock(BlockStmt block, TypeEnv parent, Z42Type retType)
    {
        var scope = parent.PushScope();
        foreach (var stmt in block.Stmts)
            CheckStmt(stmt, scope, retType);
    }

    private void CheckStmt(Stmt stmt, TypeEnv env, Z42Type retType)
    {
        switch (stmt)
        {
            case BlockStmt b:
                CheckBlock(b, env, retType);
                break;

            case VarDeclStmt v:
                CheckVarDecl(v, env);
                break;

            case ReturnStmt r:
                CheckReturn(r, env, retType);
                break;

            case ExprStmt e:
                CheckExpr(e.Expr, env);
                break;

            case IfStmt i:
            {
                var condType = CheckExpr(i.Condition, env);
                RequireBool(condType, i.Condition.Span, "if");
                CheckBlock(i.Then, env, retType);
                if (i.Else is BlockStmt eb) CheckBlock(eb, env, retType);
                else if (i.Else != null)    CheckStmt(i.Else, env, retType);
                break;
            }

            case WhileStmt w:
            {
                var condType = CheckExpr(w.Condition, env);
                RequireBool(condType, w.Condition.Span, "while");
                CheckBlock(w.Body, env, retType);
                break;
            }

            case ForStmt f:
            {
                var forScope = env.PushScope();
                if (f.Init != null) CheckStmt(f.Init, forScope, retType);
                if (f.Condition != null)
                {
                    var condType = CheckExpr(f.Condition, forScope);
                    RequireBool(condType, f.Condition.Span, "for");
                }
                if (f.Increment != null) CheckExpr(f.Increment, forScope);
                CheckBlock(f.Body, forScope, retType);
                break;
            }

            case ForeachStmt fe:
            {
                var colType  = CheckExpr(fe.Collection, env);
                var elemType = ElemTypeOf(colType);
                var feScope  = env.PushScope();
                feScope.Define(fe.VarName, elemType);
                CheckBlock(fe.Body, feScope, retType);
                break;
            }

            case BreakStmt:
            case ContinueStmt:
                break;   // valid inside any loop; loop-context check deferred to later phase

            case SwitchStmt sw:
            {
                CheckExpr(sw.Subject, env);
                foreach (var c in sw.Cases)
                {
                    if (c.Pattern != null) CheckExpr(c.Pattern, env);
                    var caseScope = env.PushScope();
                    foreach (var s in c.Body) CheckStmt(s, caseScope, retType);
                }
                break;
            }

            case TryCatchStmt tc:
            {
                CheckBlock(tc.TryBody, env, retType);
                foreach (var clause in tc.Catches)
                {
                    var catchScope = env.PushScope();
                    if (clause.VarName != null)
                        catchScope.Define(clause.VarName, Z42Type.Unknown);
                    CheckBlock(clause.Body, catchScope, retType);
                }
                if (tc.Finally != null)
                    CheckBlock(tc.Finally, env, retType);
                break;
            }

            case ThrowStmt th:
                CheckExpr(th.Value, env);
                break;
        }
        // Unknown stmt kinds are silently skipped (future constructs).
    }

    // ── Variable declarations ─────────────────────────────────────────────────

    private void CheckVarDecl(VarDeclStmt v, TypeEnv env)
    {
        Z42Type varType;

        if (v.TypeAnnotation != null)
        {
            // Explicit type: `string s = "hello";`
            varType = ResolveType(v.TypeAnnotation);
            if (v.Init != null)
            {
                var initType = CheckExpr(v.Init, env);
                RequireAssignable(varType, initType, v.Init.Span);
            }
        }
        else if (v.Init != null)
        {
            // Inferred: `var x = 42;`
            varType = CheckExpr(v.Init, env);
            if (varType is Z42VoidType)
            {
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    "cannot assign void to variable", v.Span);
                varType = Z42Type.Error;
            }
        }
        else
        {
            // `var x;` without annotation or init — unknown type, no error yet
            varType = Z42Type.Unknown;
        }

        env.Define(v.Name, varType);
    }

    // ── Return statement ──────────────────────────────────────────────────────

    private void CheckReturn(ReturnStmt r, TypeEnv env, Z42Type expectedRetType)
    {
        if (r.Value == null)
        {
            if (expectedRetType is not Z42VoidType && expectedRetType is not Z42UnknownType)
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"missing return value; function returns `{expectedRetType}`", r.Span);
            return;
        }

        var actual = CheckExpr(r.Value, env);
        RequireAssignable(expectedRetType, actual, r.Value.Span,
            $"return type mismatch: expected `{expectedRetType}`, got `{actual}`");
    }

    // ── Expression type inference ─────────────────────────────────────────────

    private Z42Type CheckExpr(Expr expr, TypeEnv env)
    {
        switch (expr)
        {
            case LitIntExpr lit:            return lit.Value is > int.MaxValue or < int.MinValue
                                                ? Z42Type.Long : Z42Type.Int;
            case LitFloatExpr:              return Z42Type.Double;
            case LitStrExpr:                return Z42Type.String;
            case LitBoolExpr:               return Z42Type.Bool;
            case LitNullExpr:               return Z42Type.Null;
            case LitCharExpr:               return Z42Type.Char;
            case InterpolatedStrExpr interp:
                // All sub-expressions must be checked; result is always string
                foreach (var p in interp.Parts)
                    if (p is ExprPart ep) CheckExpr(ep.Inner, env);
                return Z42Type.String;

            case IdentExpr id:
                return CheckIdent(id, env);

            case AssignExpr assign:
                return CheckAssign(assign, env);

            case BinaryExpr bin:
                return CheckBinary(bin, env);

            case UnaryExpr u:
                return CheckUnary(u, env);

            case PostfixExpr post:
            {
                var t = CheckExpr(post.Operand, env);
                // ++ / -- on non-numeric is a type error; return same type
                if (post.Op is "++" or "--" && !Z42Type.IsNumeric(t) && t is not Z42ErrorType and not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{post.Op}` requires numeric operand, got `{t}`", post.Span);
                return t;
            }

            case CallExpr call:
                return CheckCall(call, env);

            case MemberExpr m:
            {
                var targetType = CheckExpr(m.Target, env);
                if (targetType is Z42ClassType ct)
                {
                    if (ct.Fields.TryGetValue(m.Member, out var ft))  return ft;
                    if (ct.Methods.TryGetValue(m.Member, out var mt)) return mt;
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"type `{ct.Name}` has no member `{m.Member}`", m.Span);
                    return Z42Type.Error;
                }
                return m.Member switch
                {
                    "Length" when targetType is Z42ArrayType => Z42Type.Int,
                    "Length" when targetType == Z42Type.String => Z42Type.Int,
                    _ => Z42Type.Unknown   // other member types deferred
                };
            }

            case IndexExpr ix:
            {
                var targetType = CheckExpr(ix.Target, env);
                CheckExpr(ix.Index, env);
                return ElemTypeOf(targetType);
            }

            case ConditionalExpr ternary:
            {
                var condType = CheckExpr(ternary.Cond, env);
                RequireBool(condType, ternary.Cond.Span, "ternary");
                var thenType = CheckExpr(ternary.Then, env);
                var elseType = CheckExpr(ternary.Else, env);
                if (thenType is Z42ErrorType || elseType is Z42ErrorType) return Z42Type.Error;
                return Z42Type.IsAssignableTo(thenType, elseType) ? thenType
                     : Z42Type.IsAssignableTo(elseType, thenType) ? elseType
                     : Z42Type.Unknown;
            }

            case CastExpr cast:
                CheckExpr(cast.Operand, env);
                return ResolveType(cast.TargetType);

            case NewExpr newExpr:
            {
                foreach (var arg in newExpr.Args) CheckExpr(arg, env);
                return ResolveType(newExpr.Type);
            }

            case ArrayCreateExpr ac:
            {
                var sizeType = CheckExpr(ac.Size, env);
                if (!Z42Type.IsNumeric(sizeType) && sizeType is not Z42ErrorType and not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"array size must be numeric, got `{sizeType}`", ac.Size.Span);
                return new Z42ArrayType(ResolveType(ac.ElemType));
            }

            case ArrayLitExpr al:
            {
                var elemType = ResolveType(al.ElemType);
                foreach (var e in al.Elements)
                {
                    var et = CheckExpr(e, env);
                    RequireAssignable(elemType, et, e.Span);
                }
                return new Z42ArrayType(elemType);
            }

            case SwitchExpr sw:
            {
                CheckExpr(sw.Subject, env);
                Z42Type resultType = Z42Type.Unknown;
                foreach (var arm in sw.Arms)
                {
                    if (arm.Pattern != null) CheckExpr(arm.Pattern, env);
                    resultType = CheckExpr(arm.Body, env);
                }
                return resultType;
            }

            default:
                return Z42Type.Unknown;
        }
    }

    // ── Identifier ────────────────────────────────────────────────────────────

    private Z42Type CheckIdent(IdentExpr id, TypeEnv env)
    {
        var t = env.LookupVar(id.Name);
        if (t != null) return t;

        // Try function table (may be used as a value — e.g. in a call expression)
        var fn = env.LookupFunc(id.Name);
        if (fn != null) return fn;

        _diags.UndefinedSymbol(id.Name, id.Span);
        return Z42Type.Error;
    }

    // ── Assignment ────────────────────────────────────────────────────────────

    private Z42Type CheckAssign(AssignExpr assign, TypeEnv env)
    {
        var targetType = CheckExpr(assign.Target, env);
        var valueType  = CheckExpr(assign.Value,  env);

        RequireAssignable(targetType, valueType, assign.Value.Span);

        // After assignment, update local variable type if narrowed from Unknown
        if (assign.Target is IdentExpr id && targetType is Z42UnknownType)
            env.Define(id.Name, valueType);

        return valueType;
    }

    // ── Binary expressions ────────────────────────────────────────────────────

    private Z42Type CheckBinary(BinaryExpr bin, TypeEnv env)
    {
        var lt = CheckExpr(bin.Left,  env);
        var rt = CheckExpr(bin.Right, env);

        if (lt is Z42ErrorType || rt is Z42ErrorType) return Z42Type.Error;

        switch (bin.Op)
        {
            case "+" when lt == Z42Type.String || rt == Z42Type.String:
                // String concatenation: always valid
                return Z42Type.String;

            case "+" or "-" or "*" or "/" or "%":
                if (!Z42Type.IsNumeric(lt) && lt is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{bin.Op}` requires numeric operand, got `{lt}`", bin.Left.Span);
                else if (!Z42Type.IsNumeric(rt) && rt is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{bin.Op}` requires numeric operand, got `{rt}`", bin.Right.Span);
                return (lt is Z42ErrorType || rt is Z42ErrorType)
                    ? Z42Type.Error
                    : Z42Type.ArithmeticResult(lt, rt);

            case "<" or "<=" or ">" or ">=":
                if (!Z42Type.IsNumeric(lt) && lt is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{bin.Op}` requires numeric operand, got `{lt}`", bin.Left.Span);
                return Z42Type.Bool;

            case "==" or "!=":
                // Allowed for any matching types; skip detailed checks for Phase 1
                return Z42Type.Bool;

            case "&&" or "||":
                if (!Z42Type.IsBool(lt) && lt is not Z42UnknownType and not Z42ErrorType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{bin.Op}` requires bool operand, got `{lt}`", bin.Left.Span);
                if (!Z42Type.IsBool(rt) && rt is not Z42UnknownType and not Z42ErrorType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{bin.Op}` requires bool operand, got `{rt}`", bin.Right.Span);
                return Z42Type.Bool;

            case "is" or "as":
                return Z42Type.Bool;

            default:
                return Z42Type.Unknown;
        }
    }

    // ── Unary expressions ─────────────────────────────────────────────────────

    private Z42Type CheckUnary(UnaryExpr u, TypeEnv env)
    {
        var t = CheckExpr(u.Operand, env);
        if (t is Z42ErrorType) return Z42Type.Error;

        switch (u.Op)
        {
            case "!":
                if (!Z42Type.IsBool(t) && t is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `!` requires bool, got `{t}`", u.Operand.Span);
                return Z42Type.Bool;

            case "-" or "+":
                if (!Z42Type.IsNumeric(t) && t is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"unary `{u.Op}` requires numeric operand, got `{t}`", u.Operand.Span);
                return t;

            case "++" or "--":
                if (!Z42Type.IsNumeric(t) && t is not Z42UnknownType)
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"operator `{u.Op}` requires numeric operand, got `{t}`", u.Operand.Span);
                return t;

            case "await":
                return Z42Type.Unknown;   // async types deferred

            default:
                return t;
        }
    }

    // ── Call expressions ──────────────────────────────────────────────────────

    private static readonly HashSet<string> BuiltinPseudoClasses = ["Console", "Assert", "Math"];

    private Z42Type CheckCall(CallExpr call, TypeEnv env)
    {
        // Built-in pseudo-class calls (Console.*, Assert.*) — skip symbol resolution
        if (call.Callee is MemberExpr { Target: IdentExpr pseudoId } &&
            BuiltinPseudoClasses.Contains(pseudoId.Name))
        {
            foreach (var a in call.Args) CheckExpr(a, env);
            return Z42Type.Void;
        }

        // Member method calls
        if (call.Callee is MemberExpr mCallee)
        {
            var receiverType = CheckExpr(mCallee.Target, env);
            var argTypes2    = call.Args.Select(a => CheckExpr(a, env)).ToList();

            // User-defined class method call
            if (receiverType is Z42ClassType ct2)
            {
                if (ct2.Methods.TryGetValue(mCallee.Member, out var mt))
                {
                    if (argTypes2.Count != mt.Params.Count)
                        _diags.Error(DiagnosticCodes.TypeMismatch,
                            $"expected {mt.Params.Count} argument(s), got {argTypes2.Count}", call.Span);
                    return mt.Ret;
                }
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type `{ct2.Name}` has no method `{mCallee.Member}`", call.Span);
                return Z42Type.Error;
            }

            // String / array built-in method calls: s.Substring(...), s.Contains(...), etc.
            if (receiverType == Z42Type.String)
                return mCallee.Member switch
                {
                    "Substring"  => Z42Type.String,
                    "Contains" or "StartsWith" or "EndsWith" => Z42Type.Bool,
                    _ => Z42Type.Unknown
                };

            return Z42Type.Unknown;   // generic method call on unknown type
        }

        // Check all arguments first
        var argTypes = call.Args.Select(a => CheckExpr(a, env)).ToList();

        // Callee: try to find a known function type
        Z42Type calleeType;
        if (call.Callee is IdentExpr funcId)
        {
            // Direct function call
            var fn = env.LookupFunc(funcId.Name);
            if (fn != null)
            {
                calleeType = fn;
            }
            else
            {
                // May be undefined — CheckIdent will report it
                calleeType = CheckIdent(funcId, env);
            }
        }
        else
        {
            calleeType = CheckExpr(call.Callee, env);
        }

        if (calleeType is Z42FuncType funcType)
        {
            if (argTypes.Count != funcType.Params.Count)
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"expected {funcType.Params.Count} argument(s), got {argTypes.Count}",
                    call.Span);
            else
                for (int i = 0; i < argTypes.Count; i++)
                    RequireAssignable(funcType.Params[i], argTypes[i], call.Args[i].Span,
                        $"argument {i + 1}: expected `{funcType.Params[i]}`, got `{argTypes[i]}`");

            return funcType.Ret;
        }

        // Unknown callee type (built-in methods, member calls): return Unknown
        return Z42Type.Unknown;
    }

    // ── Type resolution ───────────────────────────────────────────────────────

    private Z42Type ResolveType(TypeExpr typeExpr) => typeExpr switch
    {
        VoidType              => Z42Type.Void,
        OptionType ot         => new Z42OptionType(ResolveType(ot.Inner)),
        ArrayType  at         => new Z42ArrayType(ResolveType(at.Element)),
        NamedType  nt         => nt.Name switch
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
                                  : new Z42PrimType(nt.Name),  // user-defined or generic
        },
        _ => Z42Type.Unknown
    };

    // ── Collection element type ───────────────────────────────────────────────

    private Z42Type ElemTypeOf(Z42Type t) => t switch
    {
        Z42ArrayType at  => at.Element,
        Z42OptionType ot => ot.Inner,
        Z42UnknownType   => Z42Type.Unknown,
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
