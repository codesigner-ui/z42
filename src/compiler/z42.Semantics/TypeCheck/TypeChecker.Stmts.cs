using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// Statement binding — part of the TypeChecker partial class.
public sealed partial class TypeChecker
{
    private int _loopDepth = 0;

    // ── Block ─────────────────────────────────────────────────────────────────

    private BoundBlock BindBlock(BlockStmt block, TypeEnv parent, Z42Type retType)
    {
        var scope = parent.PushScope();
        var stmts = new List<BoundStmt>(block.Stmts.Count);
        foreach (var stmt in block.Stmts)
            stmts.Add(BindStmt(stmt, scope, retType));
        return new BoundBlock(stmts, block.Span);
    }

    // ── Statements ────────────────────────────────────────────────────────────

    private BoundStmt BindStmt(Stmt stmt, TypeEnv env, Z42Type retType)
    {
        switch (stmt)
        {
            case BlockStmt b:
                return new BoundBlockStmt(BindBlock(b, env, retType), b.Span);

            case VarDeclStmt v:
                return BindVarDecl(v, env);

            case ReturnStmt r:
                return BindReturn(r, env, retType);

            case ExprStmt e:
                return new BoundExprStmt(BindExpr(e.Expr, env), e.Span);

            case IfStmt i:
            {
                if (i.Condition is IsPatternExpr)
                {
                    // IsPattern: push scope so binding is visible only in then-block
                    var patScope = env.PushScope();
                    var cond     = BindExpr(i.Condition, patScope);
                    RequireBool(cond.Type, i.Condition.Span, "if");
                    var then = BindBlock(i.Then, patScope, retType);
                    var els  = BindElse(i.Else, env, retType);
                    return new BoundIf(cond, then, els, i.Span);
                }
                else
                {
                    var cond = BindExpr(i.Condition, env);
                    RequireBool(cond.Type, i.Condition.Span, "if");
                    var then = BindBlock(i.Then, env, retType);
                    var els  = BindElse(i.Else, env, retType);
                    return new BoundIf(cond, then, els, i.Span);
                }
            }

            case WhileStmt w:
            {
                var cond = BindExpr(w.Condition, env);
                RequireBool(cond.Type, w.Condition.Span, "while");
                _loopDepth++;
                var body = BindBlock(w.Body, env, retType);
                _loopDepth--;
                return new BoundWhile(cond, body, w.Span);
            }

            case DoWhileStmt dw:
            {
                _loopDepth++;
                var body = BindBlock(dw.Body, env, retType);
                _loopDepth--;
                var cond = BindExpr(dw.Condition, env);
                RequireBool(cond.Type, dw.Condition.Span, "do-while");
                return new BoundDoWhile(body, cond, dw.Span);
            }

            case ForStmt f:
            {
                var forScope  = env.PushScope();
                var initBound = f.Init      != null ? BindStmt(f.Init, forScope, retType) : null;
                var condBound = f.Condition != null ? BindExpr(f.Condition, forScope)     : null;
                if (condBound != null)
                    RequireBool(condBound.Type, f.Condition!.Span, "for");
                var incrBound = f.Increment != null ? BindExpr(f.Increment, forScope)     : null;
                _loopDepth++;
                var body = BindBlock(f.Body, forScope, retType);
                _loopDepth--;
                return new BoundFor(initBound, condBound, incrBound, body, f.Span);
            }

            case ForeachStmt fe:
            {
                var coll     = BindExpr(fe.Collection, env);
                var elemType = ElemTypeOf(coll.Type);
                var feScope  = env.PushScope();
                feScope.Define(fe.VarName, elemType);
                _loopDepth++;
                var body = BindBlock(fe.Body, feScope, retType);
                _loopDepth--;
                return new BoundForeach(fe.VarName, elemType, coll, body, fe.Span);
            }

            case BreakStmt bk:
                if (_loopDepth == 0)
                    _diags.Error(DiagnosticCodes.TypeMismatch, "`break` outside of loop", bk.Span);
                return new BoundBreak(bk.Span);

            case ContinueStmt ck:
                if (_loopDepth == 0)
                    _diags.Error(DiagnosticCodes.TypeMismatch, "`continue` outside of loop", ck.Span);
                return new BoundContinue(ck.Span);

            case SwitchStmt sw:
            {
                var subject = BindExpr(sw.Subject, env);
                var cases   = new List<BoundSwitchCase>(sw.Cases.Count);
                foreach (var c in sw.Cases)
                {
                    var pattern   = c.Pattern != null ? BindExpr(c.Pattern, env) : null;
                    var caseScope = env.PushScope();
                    var caseBody  = c.Body.Select(s => BindStmt(s, caseScope, retType)).ToList();
                    cases.Add(new BoundSwitchCase(pattern, caseBody, c.Span));
                }
                return new BoundSwitch(subject, cases, sw.Span);
            }

            case TryCatchStmt tc:
            {
                var tryBody = BindBlock(tc.TryBody, env, retType);
                var catches = new List<BoundCatchClause>(tc.Catches.Count);
                foreach (var clause in tc.Catches)
                {
                    var catchScope = env.PushScope();
                    if (clause.VarName != null)
                        catchScope.Define(clause.VarName, Z42Type.Unknown);
                    catches.Add(new BoundCatchClause(clause.VarName,
                        BindBlock(clause.Body, catchScope, retType), clause.Span));
                }
                var fin = tc.Finally != null ? BindBlock(tc.Finally, env, retType) : null;
                return new BoundTryCatch(tryBody, catches, fin, tc.Span);
            }

            case ThrowStmt th:
                return new BoundThrow(BindExpr(th.Value, env), th.Span);

            default:
                // Unknown future construct — emit a no-op break
                return new BoundBreak(stmt.Span);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Binds an optional else-branch (BlockStmt or another Stmt).
    private BoundStmt? BindElse(Stmt? els, TypeEnv env, Z42Type retType) => els switch
    {
        null        => null,
        BlockStmt b => new BoundBlockStmt(BindBlock(b, env, retType), b.Span),
        _           => BindStmt(els, env, retType),
    };

    // ── Variable declaration ──────────────────────────────────────────────────

    private BoundVarDecl BindVarDecl(VarDeclStmt v, TypeEnv env)
    {
        if (env.DefinedInCurrentScope(v.Name))
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"variable `{v.Name}` is already declared in this scope", v.Span);

        Z42Type varType;
        BoundExpr? initBound = null;

        if (v.TypeAnnotation != null)
        {
            varType = ResolveType(v.TypeAnnotation);
            if (v.Init != null)
            {
                var intLitVal = ExtractIntLiteralValue(v.Init);
                if (intLitVal != null)
                {
                    var rangeOk = TryCheckIntLiteralRange(varType, intLitVal.Value, v.Init.Span);
                    initBound = BindExpr(v.Init, env, varType);
                    if (rangeOk == null)
                        RequireAssignable(varType, initBound.Type, v.Init.Span);
                }
                else
                {
                    initBound = BindExpr(v.Init, env);
                    RequireAssignable(varType, initBound.Type, v.Init.Span);
                }
            }
        }
        else if (v.Init != null)
        {
            initBound = BindExpr(v.Init, env);
            varType   = initBound.Type;
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
        return new BoundVarDecl(v.Name, varType, initBound, v.Span);
    }

    // ── Return ────────────────────────────────────────────────────────────────

    private BoundReturn BindReturn(ReturnStmt r, TypeEnv env, Z42Type expectedRetType)
    {
        if (r.Value == null)
        {
            if (expectedRetType is not Z42VoidType and not Z42UnknownType)
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"missing return value; function returns `{expectedRetType}`", r.Span);
            return new BoundReturn(null, r.Span);
        }
        var value = BindExpr(r.Value, env);
        RequireAssignable(expectedRetType, value.Type, r.Value.Span,
            $"return type mismatch: expected `{expectedRetType}`, got `{value.Type}`");
        return new BoundReturn(value, r.Span);
    }
}
