using Z42.Compiler.Diagnostics;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;

namespace Z42.Compiler.TypeCheck;

/// Statement type-checking — part of the TypeChecker partial class.
public sealed partial class TypeChecker
{
    // ── Block ─────────────────────────────────────────────────────────────────

    private void CheckBlock(BlockStmt block, TypeEnv parent, Z42Type retType)
    {
        var scope = parent.PushScope();
        foreach (var stmt in block.Stmts)
            CheckStmt(stmt, scope, retType);
    }

    // ── Statements ────────────────────────────────────────────────────────────

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
                RequireBool(CheckExpr(i.Condition, env), i.Condition.Span, "if");
                CheckBlock(i.Then, env, retType);
                if (i.Else is BlockStmt eb) CheckBlock(eb, env, retType);
                else if (i.Else != null)    CheckStmt(i.Else, env, retType);
                break;
            }

            case WhileStmt w:
                RequireBool(CheckExpr(w.Condition, env), w.Condition.Span, "while");
                CheckBlock(w.Body, env, retType);
                break;

            case DoWhileStmt dw:
                CheckBlock(dw.Body, env, retType);
                RequireBool(CheckExpr(dw.Condition, env), dw.Condition.Span, "do-while");
                break;

            case ForStmt f:
            {
                var forScope = env.PushScope();
                if (f.Init != null)      CheckStmt(f.Init, forScope, retType);
                if (f.Condition != null) RequireBool(CheckExpr(f.Condition, forScope), f.Condition.Span, "for");
                if (f.Increment != null) CheckExpr(f.Increment, forScope);
                CheckBlock(f.Body, forScope, retType);
                break;
            }

            case ForeachStmt fe:
            {
                var elemType = ElemTypeOf(CheckExpr(fe.Collection, env));
                var feScope  = env.PushScope();
                feScope.Define(fe.VarName, elemType);
                CheckBlock(fe.Body, feScope, retType);
                break;
            }

            case BreakStmt:
            case ContinueStmt:
                break; // loop-context validation deferred

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
                if (tc.Finally != null) CheckBlock(tc.Finally, env, retType);
                break;
            }

            case ThrowStmt th:
                CheckExpr(th.Value, env);
                break;
            // Unknown statement kinds silently skipped (future constructs).
        }
    }

    // ── Variable declaration ──────────────────────────────────────────────────

    private void CheckVarDecl(VarDeclStmt v, TypeEnv env)
    {
        Z42Type varType;
        if (v.TypeAnnotation != null)
        {
            varType = ResolveType(v.TypeAnnotation);
            if (v.Init != null) RequireAssignable(varType, CheckExpr(v.Init, env), v.Init.Span);
        }
        else if (v.Init != null)
        {
            varType = CheckExpr(v.Init, env);
            if (varType is Z42VoidType)
            {
                _diags.Error(DiagnosticCodes.TypeMismatch, "cannot assign void to variable", v.Span);
                varType = Z42Type.Error;
            }
        }
        else
        {
            varType = Z42Type.Unknown;
        }
        env.Define(v.Name, varType);
    }

    // ── Return statement ──────────────────────────────────────────────────────

    private void CheckReturn(ReturnStmt r, TypeEnv env, Z42Type expectedRetType)
    {
        if (r.Value == null)
        {
            if (expectedRetType is not Z42VoidType and not Z42UnknownType)
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"missing return value; function returns `{expectedRetType}`", r.Span);
            return;
        }
        var actual = CheckExpr(r.Value, env);
        RequireAssignable(expectedRetType, actual, r.Value.Span,
            $"return type mismatch: expected `{expectedRetType}`, got `{actual}`");
    }
}
