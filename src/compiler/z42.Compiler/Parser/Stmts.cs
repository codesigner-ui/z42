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
        var then = ctx.ParseBlock();
        Stmt? else_ = null;
        if (ctx.Match(TokenKind.Else))
        {
            if (ctx.Current.Kind == TokenKind.If)
                else_ = ParseIf(ctx, ctx.Advance());   // else-if chain
            else
                else_ = ctx.ParseBlock();
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
}
