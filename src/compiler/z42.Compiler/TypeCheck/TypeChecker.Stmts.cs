using Z42.Compiler.Diagnostics;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;

namespace Z42.Compiler.TypeCheck;

/// Statement type-checking — part of the TypeChecker partial class.
public sealed partial class TypeChecker
{
    private int _loopDepth = 0;
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
                // For `is` pattern bindings, check the condition in a fresh scope
                // so the binding variable is visible inside the then-block only.
                if (i.Condition is IsPatternExpr)
                {
                    var patScope = env.PushScope();
                    RequireBool(CheckExpr(i.Condition, patScope), i.Condition.Span, "if");
                    CheckBlock(i.Then, patScope, retType);
                    if (i.Else is BlockStmt eb) CheckBlock(eb, env, retType);
                    else if (i.Else != null)    CheckStmt(i.Else, env, retType);
                }
                else
                {
                    RequireBool(CheckExpr(i.Condition, env), i.Condition.Span, "if");
                    CheckBlock(i.Then, env, retType);
                    if (i.Else is BlockStmt eb) CheckBlock(eb, env, retType);
                    else if (i.Else != null)    CheckStmt(i.Else, env, retType);
                }
                break;
            }

            case WhileStmt w:
                RequireBool(CheckExpr(w.Condition, env), w.Condition.Span, "while");
                _loopDepth++;
                CheckBlock(w.Body, env, retType);
                _loopDepth--;
                break;

            case DoWhileStmt dw:
                _loopDepth++;
                CheckBlock(dw.Body, env, retType);
                _loopDepth--;
                RequireBool(CheckExpr(dw.Condition, env), dw.Condition.Span, "do-while");
                break;

            case ForStmt f:
            {
                var forScope = env.PushScope();
                if (f.Init != null)      CheckStmt(f.Init, forScope, retType);
                if (f.Condition != null) RequireBool(CheckExpr(f.Condition, forScope), f.Condition.Span, "for");
                if (f.Increment != null) CheckExpr(f.Increment, forScope);
                _loopDepth++;
                CheckBlock(f.Body, forScope, retType);
                _loopDepth--;
                break;
            }

            case ForeachStmt fe:
            {
                var elemType = ElemTypeOf(CheckExpr(fe.Collection, env));
                var feScope  = env.PushScope();
                feScope.Define(fe.VarName, elemType);
                _loopDepth++;
                CheckBlock(fe.Body, feScope, retType);
                _loopDepth--;
                break;
            }

            case BreakStmt bk:
                if (_loopDepth == 0)
                    _diags.Error(DiagnosticCodes.TypeMismatch, "`break` outside of loop", bk.Span);
                break;

            case ContinueStmt ck:
                if (_loopDepth == 0)
                    _diags.Error(DiagnosticCodes.TypeMismatch, "`continue` outside of loop", ck.Span);
                break;

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
        if (env.DefinedInCurrentScope(v.Name))
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"variable `{v.Name}` is already declared in this scope", v.Span);

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
