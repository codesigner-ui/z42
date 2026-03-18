using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser;

/// Statement handler functions. Each receives the already-consumed keyword
/// token and the parsing context; returns the parsed Stmt.
internal static class Stmts
{
    // ── var decl ─────────────────────────────────────────────────────────────

    /// `var name = expr;` or `var name;`
    public static readonly StmtFn VarDecl_ = (ctx, kw) =>
    {
        var name = ctx.Expect(TokenKind.Identifier).Text;
        Expr? init = ctx.Match(TokenKind.Eq) ? ctx.ParseExpr() : null;
        ctx.Expect(TokenKind.Semicolon);
        return new VarDeclStmt(name, null, init, kw.Span);
    };

    // ── return ────────────────────────────────────────────────────────────────

    public static readonly StmtFn Return_ = (ctx, kw) =>
    {
        Expr? val = ctx.Current.Kind == TokenKind.Semicolon ? null : ctx.ParseExpr();
        ctx.Expect(TokenKind.Semicolon);
        return new ReturnStmt(val, kw.Span);
    };

    // ── if / else ─────────────────────────────────────────────────────────────

    // Static method avoids circular delegate self-reference warning.
    private static IfStmt ParseIf(ParserContext ctx, Token kw)
    {
        ctx.Expect(TokenKind.LParen);
        var cond = ctx.ParseExpr();
        ctx.Expect(TokenKind.RParen);
        // Allow braced block or single statement body (same as C#)
        var then = ctx.Current.Kind == TokenKind.LBrace
            ? ctx.ParseBlock()
            : ctx.WrapInBlock(ctx.ParseStmt());
        Stmt? else_ = null;
        if (ctx.Match(TokenKind.Else))
        {
            if (ctx.Current.Kind == TokenKind.If)
                else_ = ParseIf(ctx, ctx.Advance());   // else-if chain
            else if (ctx.Current.Kind == TokenKind.LBrace)
                else_ = ctx.ParseBlock();
            else
                else_ = ctx.WrapInBlock(ctx.ParseStmt());
        }
        return new IfStmt(cond, then, else_, kw.Span);
    }

    public static readonly StmtFn If_ = ParseIf;

    // ── while ────────────────────────────────────────────────────────────────

    public static readonly StmtFn While_ = (ctx, kw) =>
    {
        ctx.Expect(TokenKind.LParen);
        var cond = ctx.ParseExpr();
        ctx.Expect(TokenKind.RParen);
        var body = ctx.ParseBlock();
        return new WhileStmt(cond, body, kw.Span);
    };

    // ── for ──────────────────────────────────────────────────────────────────

    public static readonly StmtFn For_ = (ctx, kw) =>
    {
        ctx.Expect(TokenKind.LParen);
        Stmt? init = ctx.Current.Kind == TokenKind.Semicolon ? null : ctx.ParseStmt();
        Expr? cond = ctx.Current.Kind == TokenKind.Semicolon ? null : ctx.ParseExpr();
        ctx.Match(TokenKind.Semicolon);
        Expr? incr = ctx.Current.Kind == TokenKind.RParen ? null : ctx.ParseExpr();
        ctx.Expect(TokenKind.RParen);
        var body = ctx.ParseBlock();
        return new ForStmt(init, cond, incr, body, kw.Span);
    };

    // ── foreach ──────────────────────────────────────────────────────────────

    public static readonly StmtFn Foreach_ = (ctx, kw) =>
    {
        ctx.Expect(TokenKind.LParen);
        ctx.Match(TokenKind.Var);   // optional var keyword
        var vname      = ctx.Expect(TokenKind.Identifier).Text;
        ctx.Expect(TokenKind.In);
        var collection = ctx.ParseExpr();
        ctx.Expect(TokenKind.RParen);
        var body = ctx.ParseBlock();
        return new ForeachStmt(vname, collection, body, kw.Span);
    };

    // ── switch statement ──────────────────────────────────────────────────────

    /// `switch (expr) { case v: stmts break; ... default: stmts break; }`
    public static readonly StmtFn Switch_ = (ctx, kw) =>
    {
        ctx.Expect(TokenKind.LParen);
        var subject = ctx.ParseExpr();
        ctx.Expect(TokenKind.RParen);
        ctx.Expect(TokenKind.LBrace);

        var cases = new List<SwitchCase>();
        while (!ctx.Check(TokenKind.RBrace) && !ctx.Check(TokenKind.Eof))
        {
            var caseSpan = ctx.Current.Span;
            Expr? pattern;
            if (ctx.Match(TokenKind.Default))
            {
                pattern = null;
                ctx.Expect(TokenKind.Colon);
            }
            else
            {
                ctx.Expect(TokenKind.Case);
                pattern = ctx.ParseExpr(11);
                ctx.Expect(TokenKind.Colon);
            }

            var body = new List<Stmt>();
            while (!ctx.Check(TokenKind.Case) && !ctx.Check(TokenKind.Default)
                && !ctx.Check(TokenKind.RBrace) && !ctx.Check(TokenKind.Eof))
            {
                body.Add(ctx.ParseStmt());
            }
            cases.Add(new SwitchCase(pattern, body, caseSpan));
        }

        ctx.Expect(TokenKind.RBrace);
        return new SwitchStmt(subject, cases, kw.Span);
    };

    // ── try / catch / finally ────────────────────────────────────────────────

    public static readonly StmtFn TryCatch_ = (ctx, kw) =>
    {
        var tryBody = ctx.ParseBlock();

        var catches = new List<CatchClause>();
        while (ctx.Match(TokenKind.Catch))
        {
            var cSpan = ctx.Current.Span;
            string? exType = null;
            string? varName = null;
            if (ctx.Match(TokenKind.LParen))
            {
                if (ctx.Current.Kind == TokenKind.Identifier)
                {
                    exType = ctx.Expect(TokenKind.Identifier).Text;
                    if (ctx.Current.Kind == TokenKind.Identifier)
                        varName = ctx.Advance().Text;
                }
                ctx.Expect(TokenKind.RParen);
            }
            var body = ctx.ParseBlock();
            catches.Add(new CatchClause(exType, varName, body, cSpan));
        }

        BlockStmt? finally_ = null;
        if (ctx.Match(TokenKind.Finally))
            finally_ = ctx.ParseBlock();

        return new TryCatchStmt(tryBody, catches, finally_, kw.Span);
    };

    // ── throw ─────────────────────────────────────────────────────────────────

    public static readonly StmtFn Throw_ = (ctx, kw) =>
    {
        var val = ctx.ParseExpr();
        ctx.Expect(TokenKind.Semicolon);
        return new ThrowStmt(val, kw.Span);
    };

    // ── break / continue ─────────────────────────────────────────────────────

    public static readonly StmtFn Break_ = (ctx, kw) =>
    {
        ctx.Match(TokenKind.Semicolon);
        return new BreakStmt(kw.Span);
    };

    public static readonly StmtFn Continue_ = (ctx, kw) =>
    {
        ctx.Match(TokenKind.Semicolon);
        return new ContinueStmt(kw.Span);
    };
}
